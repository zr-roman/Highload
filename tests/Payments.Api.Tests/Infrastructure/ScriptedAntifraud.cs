using System.Collections.Concurrent;
using Payments.Api.Antifraud;
using Payments.Api.Payments;

namespace Payments.Api.Tests.Infrastructure;

/// <summary>
/// The antifraud for the tests of the saga: every payment gets a scripted answer, every check is counted. A check of a
/// payment the test gave no answer for lands in <see cref="Unscripted"/>, as with <see cref="FakeBank"/>.
/// </summary>
public sealed class ScriptedAntifraud : IAntifraud
{
    private readonly ConcurrentDictionary<Guid, Func<Task<AntifraudDecision>>> answers = new();
    private readonly ConcurrentDictionary<Guid, int> checksByPayment = new();
    private readonly ConcurrentQueue<Guid> unscripted = new();

    /// <summary>Payments checked although the test gave no answer for them.</summary>
    public Guid[] Unscripted => unscripted.ToArray();

    public int ChecksFor(Guid paymentId) => checksByPayment.TryGetValue(paymentId, out var count) ? count : 0;

    public void Answer(Guid paymentId, AntifraudOutcome outcome) =>
        Script(paymentId, () => Task.FromResult(outcome == AntifraudOutcome.Unknown
            ? AntifraudDecision.Unknown
            : new AntifraudDecision(outcome, Score: 0, Reasons: outcome == AntifraudOutcome.Deny ? ["velocity"] : [])));

    /// <summary>Any other behaviour: a check during which someone else moves the payment on.</summary>
    public void Script(Guid paymentId, Func<Task<AntifraudDecision>> answer) => answers[paymentId] = answer;

    public async Task<AntifraudDecision> CheckAsync(Payment payment, CancellationToken cancellationToken)
    {
        checksByPayment.AddOrUpdate(payment.Id, 1, (_, count) => count + 1);

        if (!answers.TryGetValue(payment.Id, out var answer))
        {
            unscripted.Enqueue(payment.Id);
            throw new InvalidOperationException($"The test gave the antifraud no answer for payment {payment.Id}");
        }

        return await answer();
    }
}
