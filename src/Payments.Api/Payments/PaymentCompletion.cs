using Contracts;
using Microsoft.Extensions.Options;
using Payments.Api.Telemetry;

namespace Payments.Api.Payments;

public sealed class PaymentCompletion(PaymentStore store, IOptions<FeeOptions> feeOptions, PaymentsMetrics metrics, ILogger<PaymentCompletion> logger)
{
    // «Провести по одобрению банка»
    public async Task<PaymentCaptured> CaptureOnBankApprovalAsync(Payment payment, CancellationToken cancellationToken)
    {
        var fee = feeOptions.Value.Calculate(payment.Amount);
        var captured = new PaymentCaptured(payment.Id, payment.MerchantId, payment.Amount, fee, payment.Currency, DateTimeOffset.UtcNow);

        if (await store.MarkCapturedAsync(captured, cancellationToken))
            metrics.Captured(payment.Amount);
        else
            logger.LogWarning("Payment {PaymentId} left 'created' before its capture was recorded", payment.Id);

        return captured;
    }

    // «Отклонить».
    public async Task DeclineOnBankDenialAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        if (await store.MarkDeclinedAsync(paymentId, cancellationToken))
            metrics.Declined();
    }
}
