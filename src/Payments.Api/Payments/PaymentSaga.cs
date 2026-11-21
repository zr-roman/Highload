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
/// One saga for both paths: the request (<see cref="RunAsync"/>) and recovery (<see cref="ContinueAsync"/>). They
/// differ in two things only: how the bank is asked, and that recovery never fails open.
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

        if (decision.Outcome == AcquirerOutcome.Unknown)
            metrics.Unknown();

        var result = await AfterBankAsync(payment, decision.Outcome, SagaPath.Request, cancellationToken);
        return Counted(result, SagaPath.Request).Payment;
    }

    /// <summary>
    /// Recovery: goes on from where the saga of a stuck payment stopped. From 'created' it asks the bank for its
    /// decision instead of authorizing again; from 'authorized' it goes straight to the antifraud, which answers a
    /// repeated check with its stored decision. No fail open: nobody waits for recovery, so a payment without a decision
    /// of the antifraud waits for one.
    /// </summary>
    public async Task<SagaResult> ContinueAsync(Payment payment, CancellationToken cancellationToken)
    {
        switch (payment.Status)
        {
            case PaymentStatus.Created:
                var decision = await acquirer.FindDecisionAsync(payment.Id, cancellationToken);

                // 404: the authorization never reached the bank, and no money is held.
                return Counted(decision is null
                    ? await DeclineAsync(payment, "the bank has no decision", cancellationToken)
                    : await AfterBankAsync(payment, decision.Outcome, SagaPath.Recovery, cancellationToken), SagaPath.Recovery);

            case PaymentStatus.Authorized:
                return Counted(await CheckAsync(payment, SagaPath.Recovery, cancellationToken), SagaPath.Recovery);

            default:
                throw new InvalidOperationException($"Payment {payment.Id} is {payment.Status}: the saga has nothing to continue");
        }
    }

    // A saga that stopped short of a final status is counted once, where it ends, whatever step it stopped at.
    private SagaResult Counted(SagaResult result, SagaPath path)
    {
        if (result.Step is SagaStep.WaitsForBank or SagaStep.WaitsForAntifraud)
            metrics.LeftInProgress(result.Step, path);

        return result;
    }

    private async Task<SagaResult> AfterBankAsync(
        Payment payment, AcquirerOutcome outcome, SagaPath path, CancellationToken cancellationToken)
    {
        switch (outcome)
        {
            case AcquirerOutcome.Approved:
                // The money is held. 'authorized' is written before the antifraud is asked, so a process that dies
                // during the check leaves the payment where recovery goes on from the antifraud, not from the bank.
                if (!await store.MarkAuthorizedAsync(payment.Id, cancellationToken))
                    return await MovedOnAsync(payment.Id, cancellationToken);

                return await CheckAsync(payment with { Status = PaymentStatus.Authorized }, path, cancellationToken);

            case AcquirerOutcome.Declined:
                return await DeclineAsync(payment, "the bank declined it", cancellationToken);

            case AcquirerOutcome.Unknown:
                // The bank may or may not have approved: the payment waits in 'created', and recovery asks the bank.
                return new SagaResult(payment, SagaStep.WaitsForBank, "no answer from the bank");

            case AcquirerOutcome.Voided:
                // Voided before (HttpAcquirer logs it): no money is held, and approving it again is impossible.
                return await RecordVoidedAsync(payment, "the bank had voided the authorization", VoidReason.BankVoided, path,
                    cancellationToken);

            default:
                throw new InvalidOperationException($"Unexpected acquirer outcome {outcome} for payment {payment.Id}");
        }
    }

    private async Task<SagaResult> CheckAsync(Payment authorized, SagaPath path, CancellationToken cancellationToken)
    {
        var check = await antifraud.CheckAsync(authorized, cancellationToken);
        metrics.AntifraudDecision(check.Outcome, path);

        // Only the request fails open: a client waits for it. Nobody waits for recovery.
        var failOpen = path == SagaPath.Request;
        var threshold = antifraudOptions.Value.FailOpenBelowAmount;

        switch (check.Outcome)
        {
            case AntifraudOutcome.Allow:
                return await CaptureAsync(authorized, "the antifraud allowed it", cancellationToken);

            case AntifraudOutcome.Deny:
                logger.LogInformation("Antifraud denied payment {PaymentId} ({Reasons}): voiding its authorization",
                    authorized.Id, string.Join(", ", check.Reasons ?? []));
                return await VoidAsync(authorized, path, cancellationToken);

            case AntifraudOutcome.Unknown when failOpen && authorized.Amount < threshold:
                // Fail open: a small fraud costs less than a lost sale and a client kept waiting.
                logger.LogInformation(
                    "Antifraud gave no decision on payment {PaymentId}: captured unchecked, the amount {Amount} is below {FailOpenBelowAmount}",
                    authorized.Id, authorized.Amount, threshold);
                var captured = await CaptureAsync(authorized, "captured unchecked, the antifraud gave no decision", cancellationToken);
                if (captured.Step == SagaStep.Done)
                    metrics.FailOpen();

                return captured;

            case AntifraudOutcome.Unknown:
                // A large payment, or any payment in recovery, waits for a real check in 'authorized'.
                return new SagaResult(authorized, SagaStep.WaitsForAntifraud, "no decision from the antifraud");

            default:
                throw new InvalidOperationException($"Unexpected antifraud outcome {check.Outcome} for payment {authorized.Id}");
        }
    }

    private async Task<SagaResult> CaptureAsync(Payment authorized, string reason, CancellationToken cancellationToken) =>
        await completion.CaptureAsync(authorized, cancellationToken) is { } captured
            ? new SagaResult(authorized with { Status = PaymentStatus.Captured, Fee = captured.Fee }, SagaStep.Done, reason)
            : await MovedOnAsync(authorized.Id, cancellationToken);

    private async Task<SagaResult> DeclineAsync(Payment payment, string reason, CancellationToken cancellationToken) =>
        await completion.DeclineOnBankDenialAsync(payment.Id, cancellationToken)
            ? new SagaResult(payment with { Status = PaymentStatus.Declined }, SagaStep.Done, reason)
            : await MovedOnAsync(payment.Id, cancellationToken);

    // The compensation of the authorization: the bank releases the hold on the buyer's money.
    private async Task<SagaResult> VoidAsync(Payment authorized, SagaPath path, CancellationToken cancellationToken)
    {
        var outcome = await acquirer.VoidAsync(authorized, cancellationToken);

        switch (outcome)
        {
            case VoidOutcome.Voided:
                return await RecordVoidedAsync(authorized, "the antifraud denied it", VoidReason.AntifraudDeny, path, cancellationToken);

            case VoidOutcome.NotFound or VoidOutcome.Conflict:
                // The bank holds no money for the payment either way (it lost the decision, or declined the payment),
                // and for the client the payment is refused after the bank, as by a void. A repeat would answer the same.
                logger.LogWarning("Void of payment {PaymentId}: the bank answered {VoidOutcome}, recorded as voided",
                    authorized.Id, outcome);
                return await RecordVoidedAsync(authorized, $"the antifraud denied it, the void found {outcome}",
                    outcome == VoidOutcome.NotFound ? VoidReason.VoidNotFound : VoidReason.VoidConflict, path, cancellationToken);

            case VoidOutcome.Unknown:
                // The void may or may not have happened. The payment stays in 'authorized': recovery gets the stored
                // deny from the antifraud and voids again, which the bank takes as a repeat.
                logger.LogWarning("Void of payment {PaymentId}: no answer from the bank, left in 'authorized'", authorized.Id);
                return new SagaResult(authorized, SagaStep.WaitsForBank, "no answer from the bank to the void");

            default:
                throw new InvalidOperationException($"Unexpected void outcome {outcome} for payment {authorized.Id}");
        }
    }

    private async Task<SagaResult> RecordVoidedAsync(
        Payment payment, string reason, VoidReason voidReason, SagaPath path, CancellationToken cancellationToken)
    {
        if (!await store.MarkVoidedAsync(payment.Id, cancellationToken))
            return await MovedOnAsync(payment.Id, cancellationToken);

        metrics.Voided(voidReason, path);
        return new SagaResult(payment with { Status = PaymentStatus.Voided }, SagaStep.Done, reason);
    }

    // A transition found the payment no longer in its source status: someone else moved it on, recovery and the
    // request working on one payment at once. What that one wrote is the answer.
    private async Task<SagaResult> MovedOnAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        logger.LogInformation("Payment {PaymentId} moved on without this saga: answering with what is recorded", paymentId);
        var recorded = await store.FindByIdAsync(paymentId, cancellationToken)
            ?? throw new InvalidOperationException($"Payment {paymentId} is not in the database");

        return new SagaResult(recorded, SagaStep.MovedOn, "moved on by another saga");
    }
}

/// <summary>Who runs the saga: the request, which may fail open, or recovery, which never does.</summary>
public enum SagaPath
{
    Request,
    Recovery,
}

/// <summary>Why a payment ended in 'voided': for the metric, which shows the void answers 404 and 409 apart.</summary>
public enum VoidReason
{
    /// <summary>The compensation: the antifraud denied, and the bank voided the authorization.</summary>
    AntifraudDeny,

    /// <summary>The antifraud denied, and the bank had no decision to void (404): it lost it.</summary>
    VoidNotFound,

    /// <summary>The antifraud denied, and the bank had declined the payment (409): nothing to void.</summary>
    VoidConflict,

    /// <summary>The bank answered the authorization or the lookup with an authorization voided before.</summary>
    BankVoided,
}

/// <summary>Where a saga stopped.</summary>
public enum SagaStep
{
    /// <summary>This saga brought the payment to a final status.</summary>
    Done,

    /// <summary>Another saga had moved the payment on first; <see cref="SagaResult.Payment"/> is what it recorded.</summary>
    MovedOn,

    /// <summary>The bank gave no answer, to the authorization or to the void: the payment waits in progress.</summary>
    WaitsForBank,

    /// <summary>The antifraud gave no decision, and the payment did not fail open: it waits in 'authorized'.</summary>
    WaitsForAntifraud,
}

/// <summary>The payment as the saga left it, where it stopped, and why, in words for the log.</summary>
public sealed record SagaResult(Payment Payment, SagaStep Step, string Reason);
