using Microsoft.Extensions.Options;
using Payments.Api.Acquiring;
using Payments.Api.Antifraud;
using Payments.Api.Telemetry;

namespace Payments.Api.Payments;

/// <summary>
/// The orchestrator of a payment (stage 4): the bank authorizes, the antifraud checks, and the payment is captured, or
/// its authorization is voided. The state of the saga is the payment's status: each step is written before the next one
/// starts, so a process that dies anywhere leaves a status that says where to go on from. The bank and the antifraud
/// know nothing of the saga, they answer calls, and both keep their answer by payment id: a step repeated after a
/// failure gets the same answer and does no harm.
/// </summary>
/// <remarks>
/// Transient, like the typed HTTP clients of the bank and the antifraud it calls: a singleton would keep their
/// handlers for good. Orchestration rather than choreography: every step belongs to one payment of this service, and
/// the bank is an HTTP API that listens to no events of ours.
/// </remarks>
public sealed class PaymentSaga(
    PaymentStore store,
    PaymentCompletion completion,
    IAcquirer acquirer,
    IAntifraud antifraud,
    IOptions<AntifraudOptions> antifraudOptions,
    PaymentsMetrics metrics,
    ILogger<PaymentSaga> logger)
{
    /// <summary>
    /// The request path, for a payment just written in 'created'. Returns the payment as it stands now: final
    /// ('captured', 'declined', 'voided') or in progress ('created', 'authorized'), and then recovery finishes it.
    /// </summary>
    /// <param name="cancellationToken">
    /// Not the request's: once the bank is called, a client that goes away must not stop the saga before its answer is
    /// written.
    /// </param>
    public async Task<Payment> RunAsync(Payment payment, CancellationToken cancellationToken)
    {
        var decision = await acquirer.AuthorizeAsync(payment, cancellationToken);

        switch (decision.Outcome)
        {
            case AcquirerOutcome.Approved:
                return await CheckApprovedAsync(payment, cancellationToken);

            case AcquirerOutcome.Declined:
                return await completion.DeclineOnBankDenialAsync(payment.Id, cancellationToken)
                    ? payment with { Status = PaymentStatus.Declined }
                    : await ReadAsync(payment.Id, cancellationToken);

            case AcquirerOutcome.Unknown:
                // The bank may or may not have approved: the payment waits in 'created', and recovery asks the bank.
                metrics.Unknown();
                return payment;

            case AcquirerOutcome.Voided:
                // Voided before (HttpAcquirer logs it): no money is held, and approving it again is impossible.
                return await RecordVoidedAsync(payment, cancellationToken);

            default:
                throw new InvalidOperationException($"Unexpected acquirer outcome {decision.Outcome} for payment {payment.Id}");
        }
    }

    // The bank approved: the money is held. 'authorized' is written before the antifraud is asked, so a process that
    // dies during the check leaves the payment where recovery goes on from the antifraud, not from the bank.
    private async Task<Payment> CheckApprovedAsync(Payment payment, CancellationToken cancellationToken)
    {
        if (!await store.MarkAuthorizedAsync(payment.Id, cancellationToken))
            return await ReadAsync(payment.Id, cancellationToken);

        var authorized = payment with { Status = PaymentStatus.Authorized };
        var check = await antifraud.CheckAsync(authorized, cancellationToken);

        switch (check.Outcome)
        {
            case AntifraudOutcome.Allow:
                return await CaptureAsync(authorized, cancellationToken);

            case AntifraudOutcome.Deny:
                logger.LogInformation("Antifraud denied payment {PaymentId} ({Reasons}): voiding its authorization",
                    payment.Id, string.Join(", ", check.Reasons ?? []));
                return await VoidAsync(authorized, cancellationToken);

            case AntifraudOutcome.Unknown when authorized.Amount < antifraudOptions.Value.FailOpenBelowAmount:
                // Fail open: a small fraud costs less than a lost sale and a client kept waiting.
                logger.LogInformation(
                    "Antifraud gave no decision on payment {PaymentId}: captured unchecked, the amount {Amount} is below {FailOpenBelowAmount}",
                    payment.Id, authorized.Amount, antifraudOptions.Value.FailOpenBelowAmount);
                return await CaptureAsync(authorized, cancellationToken);

            case AntifraudOutcome.Unknown:
                // A large payment waits for a real check: the client gets 202, recovery asks the antifraud again.
                logger.LogInformation(
                    "Antifraud gave no decision on payment {PaymentId}: left in 'authorized', the amount {Amount} is not below {FailOpenBelowAmount}",
                    payment.Id, authorized.Amount, antifraudOptions.Value.FailOpenBelowAmount);
                return authorized;

            default:
                throw new InvalidOperationException($"Unexpected antifraud outcome {check.Outcome} for payment {payment.Id}");
        }
    }

    private async Task<Payment> CaptureAsync(Payment authorized, CancellationToken cancellationToken) =>
        await completion.CaptureAsync(authorized, cancellationToken) is { } captured
            ? authorized with { Status = PaymentStatus.Captured, Fee = captured.Fee }
            : await ReadAsync(authorized.Id, cancellationToken);

    // The compensation of the authorization: the bank releases the hold on the buyer's money.
    private async Task<Payment> VoidAsync(Payment authorized, CancellationToken cancellationToken)
    {
        var outcome = await acquirer.VoidAsync(authorized, cancellationToken);

        switch (outcome)
        {
            case VoidOutcome.Voided:
                break;

            case VoidOutcome.NotFound or VoidOutcome.Conflict:
                // The bank holds no money for the payment either way (it lost the decision, or declined the payment),
                // and for the client the payment is refused after the bank, as by a void. A repeat would answer the same.
                logger.LogWarning("Void of payment {PaymentId}: the bank answered {VoidOutcome}, recorded as voided",
                    authorized.Id, outcome);
                break;

            case VoidOutcome.Unknown:
                // The void may or may not have happened. The payment stays in 'authorized': recovery gets the stored
                // deny from the antifraud and voids again, which the bank takes as a repeat.
                logger.LogWarning("Void of payment {PaymentId}: no answer from the bank, left in 'authorized'", authorized.Id);
                return authorized;

            default:
                throw new InvalidOperationException($"Unexpected void outcome {outcome} for payment {authorized.Id}");
        }

        return await RecordVoidedAsync(authorized, cancellationToken);
    }

    private async Task<Payment> RecordVoidedAsync(Payment payment, CancellationToken cancellationToken) =>
        await store.MarkVoidedAsync(payment.Id, cancellationToken)
            ? payment with { Status = PaymentStatus.Voided }
            : await ReadAsync(payment.Id, cancellationToken);

    // A transition found the payment no longer in its source status: someone else moved it on, recovery that took the
    // payment for stuck. What it wrote is the answer.
    private async Task<Payment> ReadAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        logger.LogInformation("Payment {PaymentId} moved on without this saga: answering with what is recorded", paymentId);
        return await store.FindByIdAsync(paymentId, cancellationToken)
            ?? throw new InvalidOperationException($"Payment {paymentId} is not in the database");
    }
}
