using Microsoft.Extensions.Options;
using Npgsql;
using OpenTelemetry;
using Payments.Api.Database;
using Payments.Api.Payments;
using Payments.Api.Telemetry;
using System.Diagnostics;

namespace Payments.Api.Recovery;

public sealed class PaymentRecovery(
    PaymentStore store, 
    NpgsqlDataSource dataSource, 
    IServiceScopeFactory serviceScopeFactory, 
    IOptions<RecoveryOptions> options,
    TimeProvider timeProvider,
    PaymentsMetrics metrics,
    ILogger<PaymentRecovery> logger,
    LeaderDataSource? leaderDataSource = null) : BackgroundService
{
    private const string RecoveredTemplate = "Payment {PaymentId} recovered as {Status}: {Reason}";
    private readonly RecoveryOptions settings = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var _ = await AdvisoryLeaderLock
            .AcquireAsync(leaderDataSource?.Value ?? dataSource, AdvisoryLockKeys.PaymentRecovery, "Payment Recovery", logger, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var batch = await FindStaleBatchAsync(stoppingToken);
                var pass = await RecoverBatchAsync(batch, stoppingToken);
                await ReportOldestInProgressAsync(stoppingToken);

                if (pass.BankUnavailable || pass.AntifraudUnavailable)
                {
                    logger.LogWarning("{Dependency} unavailable, recovery paused for {Interval}",
                        pass.BankUnavailable ? "Bank" : "Antifraud", settings.Interval);
                    await Task.Delay(settings.Interval, timeProvider, stoppingToken);
                }
                else if (batch.Count < settings.BatchSize || !pass.MadeProgress)
                {
                    // Take the next batch at once only if this one was full and moved something:
                    // a full batch of payments that fail every time would otherwise spin with no pause.
                    await Task.Delay(settings.Interval, timeProvider, stoppingToken);
                }
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(e, "Разбор батча сломался, ретрай через {Interval}.", settings.Interval);
                await Task.Delay(settings.Interval, timeProvider, stoppingToken);
            }
        }
    }

    private async Task<IReadOnlyList<Payment>> FindStaleBatchAsync(CancellationToken stoppingToken)
    {
        // The poll runs every Interval and is usually empty: without this, every poll is a separate one-span trace.
        using (SuppressInstrumentationScope.Begin())
            return await store.FindStaleInProgressAsync(settings.StaleAfter, settings.BatchSize, stoppingToken);
    }

    // After the pass, so that the gauge shows what recovery left: a payment it has just finished no longer counts.
    // Every Interval at least, also while the bank or the antifraud is down: that is when payments wait longest.
    private async Task ReportOldestInProgressAsync(CancellationToken stoppingToken)
    {
        using (SuppressInstrumentationScope.Begin())
        {
            var (created, authorized) = await store.FindOldestInProgressAsync(stoppingToken);
            metrics.InProgressOldest(created, authorized);
        }
    }

    private async Task<RecoveryPass> RecoverBatchAsync(IReadOnlyList<Payment> batch, CancellationToken stoppingToken)
    {
        var pass = new RecoveryPass();

        if (batch.Count == 0)
            return pass;

        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = settings.MaxParallelism, CancellationToken = stoppingToken };
        // The saga and the HTTP clients it holds live for one pass, as they would for one request.
        using var scope = serviceScopeFactory.CreateScope();
        var saga = scope.ServiceProvider.GetRequiredService<PaymentSaga>();

        // One trace per non-empty pass: the calls of the sagas and the status updates become its children.
        using Activity? activity = PaymentsTelemetry.ActivitySource.StartActivity("payment recovery");
        activity?.SetTag("recovery.batch.size", batch.Count);

        await Parallel.ForEachAsync(batch, parallelOptions, async (payment, ct)
            => await RecoverAsync(payment, saga, pass, ct));

        if (pass.BankUnavailable)
            activity?.SetStatus(ActivityStatusCode.Error, "bank unavailable");
        else if (pass.AntifraudUnavailable)
            activity?.SetStatus(ActivityStatusCode.Error, "antifraud unavailable");

        activity?.SetTag("recovery.captured", pass.Captured);
        activity?.SetTag("recovery.declined", pass.Declined);
        activity?.SetTag("recovery.voided", pass.Voided);
        activity?.SetTag("recovery.skipped", pass.Skipped);
        activity?.SetTag("recovery.previously_resolved", pass.PreviouslyResolved);

        logger.LogInformation("Всего платежей: проведено={Captured}, отклонено={Declined}, отменено={Voided}, " +
            "пропущено={Skipped}, обработаны ранее={PreviouslyResolved}.", 
                pass.Captured, pass.Declined, pass.Voided, pass.Skipped, pass.PreviouslyResolved);

        return pass;
    }

    private async Task RecoverAsync(Payment payment, PaymentSaga saga, RecoveryPass pass, CancellationToken cancellationToken)
    {
        // The bank has not answered in this pass, or the circuit to the antifraud is open: the rest of the batch is not
        // sent to them.
        if (Volatile.Read(ref pass.BankUnavailable) || Volatile.Read(ref pass.AntifraudUnavailable))
        {
            Interlocked.Increment(ref pass.Skipped);
            return;
        }

        try
        {
            var result = await saga.ContinueAsync(payment, cancellationToken);

            switch (result.Step)
            {
                case SagaStep.Done:
                    pass.Count(result.Payment.Status);
                    logger.LogInformation(RecoveredTemplate, payment.Id, result.Payment.Status, result.Reason);
                    break;

                case SagaStep.MovedOn:
                    Interlocked.Increment(ref pass.PreviouslyResolved);
                    logger.LogInformation("Payment {PaymentId} was previously resolved", payment.Id);
                    break;

                case SagaStep.WaitsForBank:
                    Volatile.Write(ref pass.BankUnavailable, true);
                    Interlocked.Increment(ref pass.Skipped);
                    break;

                case SagaStep.WaitsForAntifraud:
                    // No fail open here: the payment waits in 'authorized' for a decision, its money held at the bank.
                    // A check without a decision (a timeout, a 503) holds back this payment alone: under load some
                    // checks fail while the rest get decisions, and a pause for each of them stalled the queue (stage 4
                    // drill, docs/load-testing.md). The antifraud is down when its circuit is open: the breaker of
                    // HttpAntifraud opens on half of the checks failing.
                    if (result.AntifraudDown)
                        Volatile.Write(ref pass.AntifraudUnavailable, true);
                    Interlocked.Increment(ref pass.Skipped);
                    break;

                default:
                    throw new InvalidOperationException($"Unexpected saga step {result.Step} for payment {payment.Id}");
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Ошибка при восстановлении платежа {PaymentId}", payment.Id);
            Interlocked.Increment(ref pass.Skipped);
        }
    }

    /// <summary>
    /// What happened during one recovery pass. 
    /// Written concurrently by the workers of Parallel.ForEachAsync.
    /// </summary>
    private sealed class RecoveryPass
    {
        public int Captured, Declined, Voided, Skipped, PreviouslyResolved;
        public bool BankUnavailable, AntifraudUnavailable;

        /// <summary>
        /// At least one payment left the queue, so the next batch is not the same one.
        /// </summary>
        public bool MadeProgress => Captured + Declined + Voided + PreviouslyResolved > 0;

        public void Count(PaymentStatus final)
        {
            switch (final)
            {
                case PaymentStatus.Captured: Interlocked.Increment(ref Captured); break;
                case PaymentStatus.Declined: Interlocked.Increment(ref Declined); break;
                case PaymentStatus.Voided: Interlocked.Increment(ref Voided); break;
                default: throw new InvalidOperationException($"{final} is not a final status");
            }
        }
    }
}
