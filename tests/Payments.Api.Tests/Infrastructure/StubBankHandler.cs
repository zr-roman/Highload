using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;

namespace Payments.Api.Tests.Infrastructure;

/// <summary>
/// The bank at the other end of HttpClient, under the real resilience pipeline. Retries, timeouts and the circuit
/// breaker all happen above it, so every request that reaches it is one attempt. Answers come from a script,
/// one per request; the last answer repeats.
/// </summary>
public sealed class StubBankHandler : HttpMessageHandler
{
    private readonly Lock gate = new();
    private readonly Queue<Func<CancellationToken, Task<HttpResponseMessage>>> script = new();
    private readonly ConcurrentQueue<BankRequest> requests = new();
    private readonly TaskCompletionSource firstRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Func<CancellationToken, Task<HttpResponseMessage>>? last;

    /// <summary>The requests that reached the bank, in order: one per attempt.</summary>
    public IReadOnlyList<BankRequest> Requests => requests.ToArray();

    /// <summary>Completes when the first request reaches the bank.</summary>
    public Task FirstRequest => firstRequest.Task;

    /// <summary>A decision as the emulator sends it. HttpAcquirer reads only the status and the code.</summary>
    public StubBankHandler Decide(string status, string? authorizationCode = "A-123") =>
        Then(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { status, authorizationCode, decidedAt = DateTimeOffset.UtcNow }),
        }));

    /// <summary>A status without a body: 404, 409, 500, 503...</summary>
    public StubBankHandler Status(HttpStatusCode status) =>
        Then(_ => Task.FromResult(new HttpResponseMessage(status)));

    /// <summary>No connection: what HttpClient throws when nothing listens on the bank's port.</summary>
    public StubBankHandler Unreachable() =>
        Then(_ => Task.FromException<HttpResponseMessage>(new HttpRequestException("Connection refused")));

    /// <summary>Takes the request and never answers: only a timeout or the caller ends it.</summary>
    public StubBankHandler Hang() =>
        Then(async cancellationToken =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new UnreachableException();
        });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        requests.Enqueue(new BankRequest(request.Method, request.RequestUri!.AbsolutePath, body));
        firstRequest.TrySetResult();

        Func<CancellationToken, Task<HttpResponseMessage>> answer;
        lock (gate)
        {
            if (script.TryDequeue(out var next))
                last = next;
            answer = last ?? throw new InvalidOperationException("The test gave the bank no answer");
        }

        return await answer(cancellationToken);
    }

    private StubBankHandler Then(Func<CancellationToken, Task<HttpResponseMessage>> answer)
    {
        lock (gate)
            script.Enqueue(answer);
        return this;
    }
}

public sealed record BankRequest(HttpMethod Method, string Path, string? Body);
