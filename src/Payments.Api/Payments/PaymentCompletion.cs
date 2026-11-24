using Contracts;
using Microsoft.Extensions.Options;
using Payments.Api.RateLimiting;
using Payments.Api.Telemetry;

namespace Payments.Api.Payments;

public sealed class PaymentCompletion(
    PaymentStore store, IOptions<FeeOptions> feeOptions, MerchantQuotas quotas, PaymentsMetrics metrics, ILogger<PaymentCompletion> logger)
{
    // «Провести»: после allow антифрода или fail open, только из 'authorized' (сага).
    public async Task<PaymentCaptured?> CaptureAsync(Payment payment, CancellationToken cancellationToken)
    {
        var fee = feeOptions.Value.Calculate(payment.Amount);

        if (await store.MarkCapturedAsync(payment.Id, fee, Key(payment), cancellationToken) is { } captured)
        {
            metrics.Captured(payment.Amount);
            return captured;
        }
        else
        {
            logger.LogWarning("Payment {PaymentId} moved on before its capture was recorded", payment.Id);
            return null;
        }
    }

    // «Отклонить».
    public async Task<bool> DeclineOnBankDenialAsync(Payment payment, CancellationToken cancellationToken)
    {
        var statusChanged = await store.MarkDeclinedAsync(payment.Id, Key(payment), cancellationToken);
        if (statusChanged)
            metrics.Declined();

        return statusChanged;
    }

    // «Отменено»: the bank released the hold. The metric of voids is the saga's, by reason (PaymentsMetrics.Voided).
    public async Task<bool> RecordVoidedAsync(Payment payment, CancellationToken cancellationToken) =>
        await store.MarkVoidedAsync(payment.Id, Key(payment), cancellationToken);

    // The key of every event of a payment. A large merchant, one with a quota of its own, has its payments spread over
    // the partitions, of payments.captured and payments.finalized alike.
    private string Key(Payment payment) =>
        Topics.PaymentsCapturedKey(payment.MerchantId, payment.Id, spread: quotas.Of(payment.MerchantId).IsOverride);
}
