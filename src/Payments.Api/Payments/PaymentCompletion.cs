using Contracts;
using Microsoft.Extensions.Options;
using Payments.Api.RateLimiting;
using Payments.Api.Telemetry;

namespace Payments.Api.Payments;

public sealed class PaymentCompletion(
    PaymentStore store, IOptions<FeeOptions> feeOptions, MerchantQuotas quotas, PaymentsMetrics metrics, ILogger<PaymentCompletion> logger)
{
    // «Провести по одобрению банка»
    public async Task<PaymentCaptured?> CaptureOnBankApprovalAsync(Payment payment, CancellationToken cancellationToken)
    {
        var fee = feeOptions.Value.Calculate(payment.Amount);
        var captured = new PaymentCaptured(payment.Id, payment.MerchantId, payment.Amount, fee, payment.Currency, DateTimeOffset.UtcNow);

        // A large merchant, one with a quota of its own, has its payments spread over the partitions of the ledger.
        var key = Topics.PaymentsCapturedKey(payment.MerchantId, payment.Id, spread: quotas.Of(payment.MerchantId).IsOverride);

        if (await store.MarkCapturedAsync(captured, key, cancellationToken))
        {
            metrics.Captured(payment.Amount);
            return captured;
        }
        else
        {
            logger.LogWarning("Payment {PaymentId} left 'created' before its capture was recorded", payment.Id);
            return null;
        }
    }

    // «Отклонить».
    public async Task<bool> DeclineOnBankDenialAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        var statusChanged = await store.MarkDeclinedAsync(paymentId, cancellationToken);
        if (statusChanged)
            metrics.Declined();

        return statusChanged;
    }
}
