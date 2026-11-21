using System.Collections.Concurrent;
using Payments.Api.Acquiring;
using Payments.Api.Payments;

namespace Payments.Api.Tests.Infrastructure;

/// <summary>
/// The bank for the tests of recovery and of the saga: every payment gets a scripted answer, every call is counted.
/// A call for a payment the test gave no answer for lands in <see cref="Unscripted"/>,
/// so the test fails loudly instead of the code quietly skipping the payment.
/// </summary>
public sealed class FakeBank : IAcquirer
{
    private readonly ConcurrentDictionary<Guid, Func<CancellationToken, Task<AcquirerDecision?>>> answers = new();
    private readonly ConcurrentDictionary<Guid, AcquirerOutcome> authorizations = new();
    private readonly ConcurrentDictionary<Guid, VoidOutcome> voidAnswers = new();
    private readonly ConcurrentDictionary<Guid, int> voidsByPayment = new();
    private readonly ConcurrentDictionary<Guid, int> lookupsByPayment = new();
    private readonly ConcurrentQueue<Guid> lookupOrder = new();
    private readonly ConcurrentQueue<Guid> unscripted = new();
    private int lookups;

    /// <summary>Decision lookups so far, for all payments.</summary>
    public int Lookups => Volatile.Read(ref lookups);

    /// <summary>Payments in the order recovery asked about them.</summary>
    public Guid[] LookupOrder => lookupOrder.ToArray();

    /// <summary>Payments recovery asked about although the test gave the bank no answer for them.</summary>
    public Guid[] Unscripted => unscripted.ToArray();

    public int LookupsFor(Guid paymentId) => lookupsByPayment.TryGetValue(paymentId, out var count) ? count : 0;

    /// <summary>Voids asked for this payment so far.</summary>
    public int VoidsFor(Guid paymentId) => voidsByPayment.TryGetValue(paymentId, out var count) ? count : 0;

    /// <summary>The answer to the authorization of the payment, the request path of the saga.</summary>
    public void AnswerAuthorization(Guid paymentId, AcquirerOutcome outcome) => authorizations[paymentId] = outcome;

    /// <summary>The answer to a void of the payment's authorization, the compensation of the saga.</summary>
    public void AnswerVoid(Guid paymentId, VoidOutcome outcome) => voidAnswers[paymentId] = outcome;

    /// <summary>The bank's decision; null means it has none (404): the authorization never reached it.</summary>
    public void Answer(Guid paymentId, AcquirerOutcome? outcome) =>
        Script(paymentId, _ => Task.FromResult(Decision(outcome)));

    public void NoDecision(Guid paymentId) => Answer(paymentId, null);

    /// <summary>One answer per lookup, in turn; the last one repeats.</summary>
    public void AnswerInTurn(Guid paymentId, params AcquirerOutcome[] outcomes)
    {
        var lookup = -1;
        Script(paymentId, _ =>
        {
            var index = Math.Min(Interlocked.Increment(ref lookup), outcomes.Length - 1);
            return Task.FromResult(Decision(outcomes[index]));
        });
    }

    /// <summary>Every lookup fails, as on a broken contract: these payments never leave 'created'.</summary>
    public void Fail(params Guid[] paymentIds)
    {
        foreach (var id in paymentIds)
        {
            Script(id, _ => Task.FromException<AcquirerDecision?>(
                new InvalidOperationException($"The bank broke its contract for payment {id}")));
        }
    }

    /// <summary>Any other behaviour: a bank that hangs, a race with the endpoint.</summary>
    public void Script(Guid paymentId, Func<CancellationToken, Task<AcquirerDecision?>> answer) => answers[paymentId] = answer;

    public async Task<AcquirerDecision?> FindDecisionAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref lookups);
        lookupsByPayment.AddOrUpdate(paymentId, 1, (_, count) => count + 1);
        lookupOrder.Enqueue(paymentId);

        if (!answers.TryGetValue(paymentId, out var answer))
        {
            unscripted.Enqueue(paymentId);
            throw new InvalidOperationException($"The test gave the bank no answer for payment {paymentId}");
        }

        return await answer(cancellationToken);
    }

    public Task<AcquirerDecision> AuthorizeAsync(Payment payment, CancellationToken cancellationToken)
    {
        if (authorizations.TryGetValue(payment.Id, out var outcome))
            return Task.FromResult(new AcquirerDecision(outcome));

        unscripted.Enqueue(payment.Id);
        throw new InvalidOperationException($"The test gave the bank no authorization for payment {payment.Id}");
    }

    public Task<VoidOutcome> VoidAsync(Payment payment, CancellationToken cancellationToken)
    {
        voidsByPayment.AddOrUpdate(payment.Id, 1, (_, count) => count + 1);
        if (voidAnswers.TryGetValue(payment.Id, out var outcome))
            return Task.FromResult(outcome);

        unscripted.Enqueue(payment.Id);
        throw new InvalidOperationException($"The test gave the bank no answer to a void of payment {payment.Id}");
    }

    private static AcquirerDecision? Decision(AcquirerOutcome? outcome) =>
        outcome is { } value ? new AcquirerDecision(value) : null;
}
