using Microsoft.AspNetCore.Mvc;
using Payments.Api.Acquiring;
using Payments.Api.RateLimiting;
using Payments.Api.Telemetry;

namespace Payments.Api.Payments;

public static class PaymentEndpoints
{
    public static IEndpointRouteBuilder MapPaymentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/payments", CreatePaymentAsync).AddEndpointFilter<MerchantRateLimitFilter>();
        app.MapGet("/payments/{id:guid}", GetPaymentAsync);
        return app;
    }

    private static async Task<IResult> CreatePaymentAsync(
        CreatePaymentRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        PaymentStore store,
        PaymentCompletion paymentCompletion,
        IAcquirer acquirer,
        PaymentsMetrics metrics,
        CancellationToken cancellationToken)
    {
        var errors = PaymentRules.Validate(request, idempotencyKey);
        if (errors.Count > 0)
            return Results.ValidationProblem(errors);

        var payment = new Payment(
            Id: Guid.CreateVersion7(),   // time-ordered UUID: new rows go to the right edge of the primary key index
            MerchantId: request.MerchantId,
            IdempotencyKey: idempotencyKey!,
            RequestHash: PaymentRules.ComputeRequestHash(request),
            Amount: request.Amount,
            Currency: request.Currency!,
            Fee: null,
            Status: PaymentStatus.Created,
            CreatedAt: DateTimeOffset.UtcNow);

        // 1. A short write before talking to the bank: if the process dies later,
        // the payment exists in 'created' and can be resolved by a recovery job.

        if (!await store.TryInsertAsync(payment, cancellationToken))
            return await ReplayAsync(store, payment, metrics, cancellationToken);

        // Step 2. The bank is called with no database transaction open (experiment 2: a waiting
        // transaction holds back vacuum). From this point on the request's cancellation token is not used:
        // if the client disconnects after the bank approved, the result must still be recorded.

        var decision = await acquirer.AuthorizeAsync(payment, CancellationToken.None);

        // Step 3. Record the outcome. For a captured payment the event goes to the outbox in the same statement.

        switch (decision.Outcome)
        {
            case AcquirerOutcome.Approved:
                var paymentCaptured = await paymentCompletion.CaptureOnBankApprovalAsync(payment, CancellationToken.None);
                if (paymentCaptured is not null)
                    return Results.Created($"/payments/{payment.Id}", PaymentResponse.From(payment with { Status = PaymentStatus.Captured, Fee = paymentCaptured.Fee }));

                return await CreateResponseWithPaymentFromStoreAsync(store, payment.Id);

            case AcquirerOutcome.Declined:
                if (await paymentCompletion.DeclineOnBankDenialAsync(payment.Id, CancellationToken.None))
                    return Results.Created($"/payments/{payment.Id}", PaymentResponse.From(payment with { Status = PaymentStatus.Declined }));

                return await CreateResponseWithPaymentFromStoreAsync(store, payment.Id);

            case AcquirerOutcome.Unknown:
                metrics.Unknown();
                return Results.Accepted($"/payments/{payment.Id}", PaymentResponse.From(payment));

            default:
                throw new InvalidOperationException($"Unexpected acquirer outcome {decision.Outcome} for payment {payment.Id}");
        }
    }

    private static async Task<IResult> CreateResponseWithPaymentFromStoreAsync(PaymentStore store, Guid paymentId) 
    {
        var paymentFromStore = await store.FindByIdAsync(paymentId, CancellationToken.None);
        return Results.Created($"/payments/{paymentId}", PaymentResponse.From(paymentFromStore ?? throw new InvalidOperationException($"Не найден платеж в базе c paymentId={paymentId}.")));
    }

    /// <summary>The same Idempotency-Key came again: answer with the existing payment instead of paying twice.</summary>
    private static async Task<IResult> ReplayAsync(
        PaymentStore store, Payment attempted, PaymentsMetrics metrics, CancellationToken cancellationToken)
    {
        var existing = await store.FindByIdempotencyKeyAsync(attempted.MerchantId, attempted.IdempotencyKey, cancellationToken);

        if (existing is null)
            return Results.Problem("The payment for this Idempotency-Key was not found, retry the request.",
                statusCode: StatusCodes.Status409Conflict);

        if (existing.RequestHash != attempted.RequestHash)
            return Results.Problem("This Idempotency-Key was already used with a different request.",
                statusCode: StatusCodes.Status422UnprocessableEntity);

        if (existing.Status == PaymentStatus.Created)
        {
            metrics.Replayed();
            return Results.Accepted($"/payments/{existing.Id}", PaymentResponse.From(existing));
        }

        metrics.Replayed();
        return Results.Ok(PaymentResponse.From(existing));
    }

    private static async Task<IResult> GetPaymentAsync(Guid id, PaymentStore store, CancellationToken cancellationToken)
    {
        var payment = await store.FindByIdAsync(id, cancellationToken);
        return payment is null ? Results.NotFound() : Results.Ok(PaymentResponse.From(payment));
    }
}
