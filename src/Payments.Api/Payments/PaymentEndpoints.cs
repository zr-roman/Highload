using Microsoft.AspNetCore.Mvc;
using Payments.Api.RateLimiting;
using Payments.Api.Telemetry;

namespace Payments.Api.Payments;

public static class PaymentEndpoints
{
    public static IEndpointRouteBuilder MapPaymentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/payments", CreatePaymentAsync).AddEndpointFilter<MerchantRateLimitFilter>();
        app.MapGet("/payments/{id:guid}", GetPaymentAsync);
        app.MapGet("/merchants/{merchantId:long}/payments", ListPaymentsAsync);
        return app;
    }

    private static async Task<IResult> CreatePaymentAsync(
        CreatePaymentRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        PaymentStore store,
        PaymentSaga saga,
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

        // Step 2. The saga: the bank, the antifraud, then capture or void. No database transaction is open while they
        // answer (experiment 2: a waiting transaction holds back vacuum). From this point on the request's
        // cancellation token is not used: if the client disconnects after the bank approved, the result must still be
        // recorded.

        var result = await saga.RunAsync(payment, CancellationToken.None);

        // Step 3. A final status is the answer. A payment still in progress, the bank or the antifraud silent, gets 202:
        // recovery finishes it, and the client asks GET /payments/{id}.

        return result.Status is PaymentStatus.Created or PaymentStatus.Authorized
            ? Results.Accepted($"/payments/{result.Id}", PaymentResponse.From(result))
            : Results.Created($"/payments/{result.Id}", PaymentResponse.From(result));
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

        // Still in progress: the same 202 as the first request whose outcome is not known yet.
        if (existing.Status is PaymentStatus.Created or PaymentStatus.Authorized)
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

    /// <summary>
    /// A page of the merchant's payments, newest first (PaymentList). A merchant with no payments gets an empty page,
    /// not 404: the API has no list of merchants to tell an unknown one from one that has not paid yet.
    /// </summary>
    private static async Task<IResult> ListPaymentsAsync(
        long merchantId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? status,
        string? cursor,
        int? limit,
        PaymentList list,
        CancellationToken cancellationToken)
    {
        var query = PaymentListQuery.TryParse(merchantId, from, to, status, cursor, limit, out var errors);
        return query is null
            ? Results.ValidationProblem(errors)
            : Results.Ok(await list.PageAsync(query, cancellationToken));
    }
}
