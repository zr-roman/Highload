using Microsoft.Extensions.Options;
using Npgsql;
using Payments.Api.Acquiring;
using Payments.Api.Database;
using Payments.Api.Payments;

namespace Payments.Api.Recovery;

public sealed class PaymentRecovery(
    PaymentStore store, 
    NpgsqlDataSource dataSource, 
    PaymentCompletion paymentCompletion, 
    IServiceScopeFactory serviceScopeFactory, 
    IOptions<RecoveryOptions> options, 
    ILogger<PaymentRecovery> logger) : BackgroundService
{
    private int previouslyResolved, captured, declined, skipped;
    private bool bankUnavailable;

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
        previouslyResolved = 0; captured = 0; declined = 0; skipped = 0; bankUnavailable = false;

        // Получить батч через FindStaleCreatedAsync.
        var batch = await store.FindStaleCreatedAsync(options.Value.StaleAfter, options.Value.BatchSize, stoppingToken);

        // Разобрать платежи параллельно, с ограничением MaxParallelism.
        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = options.Value.MaxParallelism, CancellationToken = stoppingToken };

        using var scope = serviceScopeFactory.CreateScope();

        var acquirer = scope.ServiceProvider.GetRequiredService<IAcquirer>();

        await Parallel.ForEachAsync(batch, parallelOptions, async (payment, ct) 
            => await RecoverAsync(payment, acquirer, ct));

        if (batch.Count > 0)
            logger.LogInformation("Всего платежей: проведено={Captured}, отклонено={Declined}, пропущено={Skipped}, обработаны ранее={PreviouslyResolved}.", captured, declined, skipped, previouslyResolved);

        if (Volatile.Read(ref bankUnavailable))
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

    private async Task RecoverAsync(Payment payment, IAcquirer acquirer, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref bankUnavailable))
        {
            Interlocked.Increment(ref skipped);
            return;
        }

        void MarkPreviouslyResolved()
        {
            Interlocked.Increment(ref previouslyResolved);
            logger.LogInformation("Payment {PaymentId} was previously resolved", payment.Id); 
        }

        try
        {
            var decision = await acquirer.FindDecisionAsync(payment.Id, cancellationToken);

            if (decision is null)
            {
                if (await paymentCompletion.DeclineOnBankDenialAsync(payment.Id, cancellationToken))
                {
                    Interlocked.Increment(ref declined);
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
                        Interlocked.Increment(ref captured);
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
                        Interlocked.Increment(ref declined);
                        logger.LogInformation(infoMsg, payment.Id, decision.Outcome);
                    }
                    else
                    {
                        MarkPreviouslyResolved();
                    }
                    break;

                case AcquirerOutcome.Unknown:
                    Volatile.Write(ref bankUnavailable, true);
                    Interlocked.Increment(ref skipped);
                    return;

                default:
                    throw new InvalidOperationException($"Unexpected acquirer outcome {decision.Outcome} for payment {payment.Id}");
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Ошибка при восстановлении платежа {PaymentId}", payment.Id);
            Interlocked.Increment(ref skipped);
        }
    }
}
