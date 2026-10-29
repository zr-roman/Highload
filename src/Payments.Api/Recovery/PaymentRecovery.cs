using Microsoft.Extensions.Options;
using Npgsql;
using OpenTelemetry;
using Payments.Api.Acquiring;
using Payments.Api.Database;
using Payments.Api.Payments;
using Payments.Api.Telemetry;
using System.Diagnostics;

namespace Payments.Api.Recovery;

public sealed class PaymentRecovery(
    PaymentStore store, 
    NpgsqlDataSource dataSource, 
    PaymentCompletion paymentCompletion, 
    IServiceScopeFactory serviceScopeFactory, 
    IOptions<RecoveryOptions> options,
    ILogger<PaymentRecovery> logger) : BackgroundService
{
    private const string RecoveredTemplate = "Payment {PaymentId} recovered as {Status}: {Reason}";
    private readonly RecoveryOptions settings = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var _ = await AdvisoryLeaderLock
            .AcquireAsync(dataSource, AdvisoryLockKeys.PaymentRecovery, "Payment Recovery", logger, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var batch = await FindStaleBatchAsync(stoppingToken);
                var pass = await RecoverBatchAsync(batch, stoppingToken);

                if (pass.BankUnavailable)
                {
                    logger.LogWarning("Bank unavailable, recovery paused for {Interval}", settings.Interval);
                    await Task.Delay(settings.Interval, stoppingToken);
                }
                else if (batch.Count < settings.BatchSize || !pass.MadeProgress)
                {
                    // Take the next batch at once only if this one was full and moved something:
                    // a full batch of payments that fail every time would otherwise spin with no pause.
                    await Task.Delay(settings.Interval, stoppingToken);
                }
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(e, "Разбор батча сломался, ретрай через {Interval}.", settings.Interval);
                await Task.Delay(settings.Interval, stoppingToken);
            }
        }
    }

    private async Task<IReadOnlyList<Payment>> FindStaleBatchAsync(CancellationToken stoppingToken)
    {
        // The poll runs every Interval and is usually empty: without this, every poll is a separate one-span trace.
        using (SuppressInstrumentationScope.Begin())
            return await store.FindStaleCreatedAsync(settings.StaleAfter, settings.BatchSize, stoppingToken);
    }

    private async Task<RecoveryPass> RecoverBatchAsync(IReadOnlyList<Payment> batch, CancellationToken stoppingToken)
    {
        var pass = new RecoveryPass();

        if (batch.Count == 0)
            return pass;

        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = settings.MaxParallelism, CancellationToken = stoppingToken };
        using var scope = serviceScopeFactory.CreateScope();
        var acquirer = scope.ServiceProvider.GetRequiredService<IAcquirer>();

        // One trace per non-empty pass: bank lookups and status updates become its children.
        using Activity? activity = PaymentsTelemetry.ActivitySource.StartActivity("payment recovery");
        activity?.SetTag("recovery.batch.size", batch.Count);

        await Parallel.ForEachAsync(batch, parallelOptions, async (payment, ct)
            => await RecoverAsync(payment, acquirer, pass, ct));

        if (pass.BankUnavailable)
            activity?.SetStatus(ActivityStatusCode.Error, "bank unavailable");

        activity?.SetTag("recovery.captured", pass.Captured);
        activity?.SetTag("recovery.declined", pass.Declined);
        activity?.SetTag("recovery.skipped", pass.Skipped);
        activity?.SetTag("recovery.previously_resolved", pass.PreviouslyResolved);

        logger.LogInformation("Всего платежей: проведено={Captured}, отклонено={Declined}, " +
            "пропущено={Skipped}, обработаны ранее={PreviouslyResolved}.", 
                pass.Captured, pass.Declined, pass.Skipped, pass.PreviouslyResolved);

        return pass;
    }

    private async Task RecoverAsync(Payment payment, IAcquirer acquirer, RecoveryPass pass, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref pass.BankUnavailable))
        {
            Interlocked.Increment(ref pass.Skipped);
            return;
        }

        try
        {
            var decision = await acquirer.FindDecisionAsync(payment.Id, cancellationToken);

            switch (decision?.Outcome)
            {
                case AcquirerOutcome.Approved:
                    await CaptureAsync(payment, pass, cancellationToken);
                    break;

                case null:
                    await DeclineAsync(payment, pass, "the bank has no decision", cancellationToken);
                    break;

                case AcquirerOutcome.Declined:
                    await DeclineAsync(payment, pass, "the bank declined it", cancellationToken);
                    break;

                case AcquirerOutcome.Unknown:
                    Volatile.Write(ref pass.BankUnavailable, true);
                    Interlocked.Increment(ref pass.Skipped);
                    return;

                default:
                    throw new InvalidOperationException($"Unexpected acquirer outcome {decision.Outcome} for payment {payment.Id}");
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Ошибка при восстановлении платежа {PaymentId}", payment.Id);
            Interlocked.Increment(ref pass.Skipped);
        }
    }

    private async Task CaptureAsync(Payment payment, RecoveryPass pass, CancellationToken cancellationToken)
    {
        if (await paymentCompletion.CaptureOnBankApprovalAsync(payment, cancellationToken) is not null)
        {
            Interlocked.Increment(ref pass.Captured);
            logger.LogInformation(RecoveredTemplate, payment.Id, PaymentStatus.Captured, "the bank approved it");
        }
        else
        {
            MarkPreviouslyResolved(payment, pass);
        }
    }

    private async Task DeclineAsync(Payment payment, RecoveryPass pass, string reason, CancellationToken cancellationToken)
    {
        if (await paymentCompletion.DeclineOnBankDenialAsync(payment.Id, cancellationToken))
        {
            Interlocked.Increment(ref pass.Declined);
            logger.LogInformation(RecoveredTemplate, payment.Id, PaymentStatus.Declined, reason);
        }
        else
        {
            MarkPreviouslyResolved(payment, pass);
        }
    }

    private void MarkPreviouslyResolved(Payment payment, RecoveryPass pass)
    {
        Interlocked.Increment(ref pass.PreviouslyResolved);
        logger.LogInformation("Payment {PaymentId} was previously resolved", payment.Id);
    }

    /// <summary>
    /// What happened during one recovery pass. 
    /// Written concurrently by the workers of Parallel.ForEachAsync.
    /// </summary>
    private sealed class RecoveryPass
    {
        public int Captured, Declined, Skipped, PreviouslyResolved;
        public bool BankUnavailable;

        /// <summary>
        /// At least one payment left 'created', so the next batch is not the same one.
        /// </summary>
        public bool MadeProgress => Captured + Declined + PreviouslyResolved > 0;
    }
}
