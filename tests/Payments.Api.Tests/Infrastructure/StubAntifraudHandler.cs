using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;

namespace Payments.Api.Tests.Infrastructure;

/// <summary>
/// The antifraud service at the other end of HttpClient, under the real resilience pipeline: every request that
/// reaches it is one attempt. Answers come from a script, one per request; the last answer repeats.
/// </summary>
public sealed class StubAntifraudHandler : HttpMessageHandler
{
    private readonly Lock gate = new();
    private readonly Queue<Func<CancellationToken, Task<HttpResponseMessage>>> script = new();
    private readonly ConcurrentQueue<BankRequest> requests = new();
    private Func<CancellationToken, Task<HttpResponseMessage>>? last;

    /// <summary>The requests that reached antifraud, in order: one per attempt.</summary>
    public IReadOnlyList<BankRequest> Requests => requests.ToArray();

    /// <summary>A decision as the service sends it.</summary>
    public StubAntifraudHandler Decide(string decision, double score = 0, string[]? reasons = null, string modelVersion = "rules-2") =>
        Then(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { paymentId = Guid.Empty, decision, score, reasons = reasons ?? [], modelVersion }),
        }));

    /// <summary>A status with an error body, as the service sends it: 400, 500, 503...</summary>
    public StubAntifraudHandler Status(HttpStatusCode status) =>
        Then(_ => Task.FromResult(new HttpResponseMessage(status) { Content = JsonContent.Create(new { error = "stub" }) }));

    /// <summary>Takes the request and never answers: only a timeout or the caller ends it.</summary>
    public StubAntifraudHandler Hang() =>
        Then(async cancellationToken =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new UnreachableException();
        });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        requests.Enqueue(new BankRequest(request.Method, request.RequestUri!.AbsolutePath, body));

        Func<CancellationToken, Task<HttpResponseMessage>> answer;
        lock (gate)
        {
            if (script.TryDequeue(out var next))
                last = next;
            answer = last ?? throw new InvalidOperationException("The test gave antifraud no answer");
        }

        return await answer(cancellationToken);
    }

    private StubAntifraudHandler Then(Func<CancellationToken, Task<HttpResponseMessage>> answer)
    {
        lock (gate)
            script.Enqueue(answer);
        return this;
    }
}
