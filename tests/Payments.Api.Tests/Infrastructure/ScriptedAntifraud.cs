using System.Collections.Concurrent;
using Payments.Api.Antifraud;
using Payments.Api.Payments;

namespace Payments.Api.Tests.Infrastructure;

/// <summary>
/// The antifraud for the tests of the saga and of recovery: every payment gets a scripted answer, every check is
/// counted. A check of a payment the test gave no answer for lands in <see cref="Unscripted"/>, as with
/// <see cref="FakeBank"/>, unless the antifraud was made with an answer for everything else.
/// </summary>
/// <param name="otherwise">The answer for payments without one of their own: for tests about something else.</param>
public sealed class ScriptedAntifraud(AntifraudOutcome? otherwise = null) : IAntifraud
{
    private readonly ConcurrentDictionary<Guid, Func<Task<AntifraudDecision>>> answers = new();
    private readonly ConcurrentDictionary<Guid, int> checksByPayment = new();
    private readonly ConcurrentQueue<Guid> unscripted = new();

    /// <summary>Payments checked although the test gave no answer for them.</summary>
    public Guid[] Unscripted => unscripted.ToArray();

    public int ChecksFor(Guid paymentId) => checksByPayment.TryGetValue(paymentId, out var count) ? count : 0;

    public void Answer(Guid paymentId, AntifraudOutcome outcome) => Script(paymentId, () => Task.FromResult(Decision(outcome)));

    /// <summary>One answer per check, the last one for every check after them: down at first, then back.</summary>
    public void AnswerInTurn(Guid paymentId, params AntifraudOutcome[] outcomes) =>
        AnswerInTurn(paymentId, outcomes.Select(Decision).ToArray());

    /// <summary>The same with whole decisions: <see cref="AntifraudDecision.Unavailable"/>, the circuit open.</summary>
    public void AnswerInTurn(Guid paymentId, params AntifraudDecision[] decisions)
    {
        var turn = -1;
        Script(paymentId, () => Task.FromResult(decisions[Math.Min(Interlocked.Increment(ref turn), decisions.Length - 1)]));
    }

    /// <summary>Any other behaviour: a check during which someone else moves the payment on.</summary>
    public void Script(Guid paymentId, Func<Task<AntifraudDecision>> answer) => answers[paymentId] = answer;

    public async Task<AntifraudDecision> CheckAsync(Payment payment, CancellationToken cancellationToken)
    {
        checksByPayment.AddOrUpdate(payment.Id, 1, (_, count) => count + 1);

        if (answers.TryGetValue(payment.Id, out var answer))
            return await answer();

        if (otherwise is { } outcome)
            return Decision(outcome);

        unscripted.Enqueue(payment.Id);
        throw new InvalidOperationException($"The test gave the antifraud no answer for payment {payment.Id}");
    }

    /// <summary>The decision the antifraud gives with this outcome; Unknown is one check without a decision.</summary>
    public static AntifraudDecision Decision(AntifraudOutcome outcome) => outcome == AntifraudOutcome.Unknown
        ? AntifraudDecision.Unknown
        : new AntifraudDecision(outcome, Score: 0, Reasons: outcome == AntifraudOutcome.Deny ? ["velocity"] : []);
}
