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
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var _ = await AdvisoryLeaderLock.AcquireAsync(dataSource, AdvisoryLockKeys.PaymentRecovery, "Payment Recovery", logger, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Разбор батча сломался, ретрай через {Interval}.", options.Value.Interval);
                await Task.Delay(options.Value.Interval, stoppingToken);
            }
        }
    }

    private async Task ProcessBatchAsync(CancellationToken stoppingToken)
    {
        var pass = new RecoveryPass();

        // Получить батч через FindStaleCreatedAsync.
        // The poll runs every Interval and is usually empty: without this, every poll is a separate one-span trace.
        IReadOnlyList<Payment> batch;
        using (SuppressInstrumentationScope.Begin())
            batch = await store.FindStaleCreatedAsync(options.Value.StaleAfter, options.Value.BatchSize, stoppingToken);

        if (batch.Count > 0)
        {
            // Разобрать платежи параллельно, с ограничением MaxParallelism.
            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = options.Value.MaxParallelism, CancellationToken = stoppingToken };
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
        }

        if (pass.BankUnavailable)
        {
            logger.LogWarning("Bank unavailable, recovery paused for {Interval}", options.Value.Interval);
            await Task.Delay(options.Value.Interval, stoppingToken);
        }
        else
        {
            // Если батч был неполный, подождать Interval.
            if (batch.Count < options.Value.BatchSize)
                await Task.Delay(options.Value.Interval, stoppingToken);
        }
    }

    private async Task RecoverAsync(Payment payment, IAcquirer acquirer, RecoveryPass pass, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref pass.BankUnavailable))
        {
            Interlocked.Increment(ref pass.Skipped);
            return;
        }

        void MarkPreviouslyResolved()
        {
            Interlocked.Increment(ref pass.PreviouslyResolved);
            logger.LogInformation("Payment {PaymentId} was previously resolved", payment.Id); 
        }

        try
        {
            var decision = await acquirer.FindDecisionAsync(payment.Id, cancellationToken);

            if (decision is null)
            {
                if (await paymentCompletion.DeclineOnBankDenialAsync(payment.Id, cancellationToken))
                {
                    Interlocked.Increment(ref pass.Declined);
                    logger.LogInformation("Payment {PaymentId} recovered as Declined: the bank has no decision", payment.Id);
                }
                else
                {
                    MarkPreviouslyResolved();
                }
                return;
            }

            const string infoMsg = "Payment {PaymentId} recovered as {Outcome}.";

            switch (decision.Outcome)
            {
                case AcquirerOutcome.Approved:
                    if (await paymentCompletion.CaptureOnBankApprovalAsync(payment, cancellationToken) is not null)
                    {
                        Interlocked.Increment(ref pass.Captured);
                        logger.LogInformation(infoMsg, payment.Id, decision.Outcome);
                    }
                    else
                    {
                        MarkPreviouslyResolved();
                    }
                    break;

                case AcquirerOutcome.Declined:
                    if (await paymentCompletion.DeclineOnBankDenialAsync(payment.Id, cancellationToken))
                    {
                        Interlocked.Increment(ref pass.Declined);
                        logger.LogInformation(infoMsg, payment.Id, decision.Outcome);
                    }
                    else
                    {
                        MarkPreviouslyResolved();
                    }
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

    /// <summary>
    /// What happened during one recovery pass. Written concurrently by the workers of Parallel.ForEachAsync.
    /// </summary>
    private sealed class RecoveryPass
    {
        public int Captured, Declined, Skipped, PreviouslyResolved;
        public bool BankUnavailable;
    }
}
