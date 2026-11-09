using System.Text.Json;

namespace Matrix;

/// <summary>
/// One load of the stage 3 matrix: payments per second, the hot merchant's share of them and whether the merchant
/// rate limit is on. A share of 0 is the baseline: the hot merchant gets nothing and the other merchants split
/// everything equally, as they split the rest in the runs with a hot merchant.
/// </summary>
public sealed record MatrixRun(int Id, int Rate, double HotShare, bool Limiter)
{
    public static readonly IReadOnlyList<MatrixRun> All =
    [
        new(1, Rate: 1000, HotShare: 0, Limiter: true),
        new(2, Rate: 1000, HotShare: 0.5, Limiter: true),
        new(3, Rate: 1000, HotShare: 0.9, Limiter: true),
        new(4, Rate: 1500, HotShare: 0, Limiter: true),
        new(5, Rate: 1500, HotShare: 0.9, Limiter: true),
        // Without the limit all payments of the hot merchant reach its partition of payments.captured.
        new(6, Rate: 1500, HotShare: 0.9, Limiter: false),
    ];

    public bool HasHotMerchant => HotShare > 0;
}

/// <summary>
/// What one repeat of a run measured. The label is the run and the repeat: 3a, 3b. Ledger is null if /invariants did not answer.
/// LedgerMembers are the partitions of each instance of Ledger when the repeat began; null if Kafka did not tell.
/// </summary>
public sealed record RunResult(
    MatrixRun Run,
    string Label,
    int DurationSeconds,
    K6Result K6,
    BacklogSummary Backlog,
    int LimiterOutages,
    LedgerCheck? Ledger,
    IReadOnlyList<IReadOnlyList<int>>? LedgerMembers)
{
    /// <summary>Payments of the hot merchant the service took, 201 and 202: all its requests but 429 and failures.</summary>
    public long HotAccepted => K6.HotRequests - K6.HotRateLimited - K6.HotFailed;

    /// <summary>
    /// How many more payments the hot merchant got than its token bucket lets through in the run: the burst at the start
    /// plus the rate for every second. Above zero, the limiter let payments through without counting them.
    /// </summary>
    public long OverLimit(StandSettings settings) =>
        Math.Max(0, HotAccepted - ((long)settings.HotTokensPerSecond * DurationSeconds + settings.HotBurst));

    /// <summary>The partition of the hot merchant: the one that got the most messages. Null if the run has no hot merchant.</summary>
    public int? HotPartition => Run.HasHotMerchant ? Array.IndexOf(Backlog.Messages, Backlog.Messages.Max()) : null;

    public long? HotPartitionLagMax => HotPartition is { } partition ? Backlog.LagMax[partition] : null;

    public long OtherPartitionsLagMax => Backlog.LagMax.Where((_, partition) => partition != HotPartition).Max();

    public double? HotPartitionMessagesPerSecond => HotPartition is { } partition ? (double)Backlog.Messages[partition] / DurationSeconds : null;

    public double OtherPartitionsMessagesPerSecond => Backlog.Messages.Where((_, partition) => partition != HotPartition).Average() / DurationSeconds;

    /// <summary>The fastest the ledger wrote the hot partition (BacklogSummary.PeakWrite). Null if the run has no hot merchant.</summary>
    public double? HotPartitionPeakWrite => HotPartition is { } partition ? Backlog.PeakWrite[partition] : null;

    /// <summary>Messages a second that came to the partitions of one instance of Ledger.</summary>
    public double MessagesPerSecond(IEnumerable<int> partitions) => (double)partitions.Sum(partition => Backlog.Messages[partition]) / DurationSeconds;
}

/// <summary>The answer of /invariants of the ledger: whether they hold, how many payments the ledger has, how long it took.</summary>
public sealed record LedgerCheck(bool Ok, long Payments, TimeSpan Took)
{
    public static LedgerCheck Read(string json, TimeSpan took)
    {
        using var answer = JsonDocument.Parse(json);
        return new LedgerCheck(answer.RootElement.GetProperty("ok").GetBoolean(), answer.RootElement.GetProperty("payments").GetInt64(), took);
    }
}
