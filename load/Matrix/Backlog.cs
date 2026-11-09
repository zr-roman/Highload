using System.Diagnostics;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Contracts;
using Npgsql;

namespace Matrix;

/// <summary>
/// The backlog that k6 cannot see, sampled every second: how long the oldest outbox row has waited
/// (payments.outbox.oldest_age) and how many messages of each partition the ledger has not committed yet
/// (ledger.consumer.lag, as kafka-consumer-groups shows it). The dashboard has these gauges only at metric exports.
/// <paramref name="ensureStand"/> runs before every sample: a process of the stand that has died ends a wait at once,
/// not after the timeout.
/// </summary>
public sealed class Backlog(NpgsqlDataSource payments, IAdminClient kafka, string ledgerGroup, Action ensureStand)
{
    private static readonly List<TopicPartition> Partitions = Enumerable.Range(0, Topics.PaymentsCapturedPartitions)
        .Select(partition => new TopicPartition(Topics.PaymentsCaptured, partition))
        .ToList();

    private static readonly TimeSpan CatchUpTimeout = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan KafkaTimeout = TimeSpan.FromSeconds(5);

    // A ledger gets its partitions a fraction of a second after it starts.
    private static readonly TimeSpan AssignmentTimeout = TimeSpan.FromMinutes(1);

    /// <summary>A sample that takes longer is left out of the record (TrySampleAsync).</summary>
    public static readonly TimeSpan SlowSample = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Waits until the ledger's group holds every partition of payments.captured, shared by all its <paramref name="members"/>.
    /// A ledger left without them consumes nothing, and the wait for a catch-up would only end after 10 min: so it went when
    /// the group coordinator of Kafka stopped answering, and the ledger lost its partitions and never got them back.
    /// A ledger that joins later would take its partitions from the others in the middle of a run.
    /// </summary>
    public async Task WaitLedgerAssignedAsync(int members, CancellationToken ct)
    {
        var waited = Stopwatch.StartNew();
        while (true)
        {
            ensureStand();

            string state;
            try
            {
                var group = (await kafka.DescribeConsumerGroupsAsync(
                    [ledgerGroup], new DescribeConsumerGroupsOptions { RequestTimeout = KafkaTimeout })).ConsumerGroupDescriptions.Single();

                var assigned = group.Members
                    .SelectMany(member => member.Assignment.TopicPartitions)
                    .Count(partition => partition.Topic == Topics.PaymentsCaptured);

                if (group.State == ConsumerGroupState.Stable && group.Members.Count == members && assigned == Partitions.Count)
                    return;

                state = $"the group is {group.State}, {group.Members.Count} member(s), {assigned} of {Partitions.Count} partitions assigned";
            }
            catch (KafkaException e)
            {
                state = e.Message;
            }

            if (waited.Elapsed > AssignmentTimeout)
                throw new MatrixException(
                    $"The ledger has not got the partitions of {Topics.PaymentsCaptured} in {AssignmentTimeout.TotalMinutes:0} min ({state}). " +
                    "Restart Kafka, docker restart payments-kafka, and start again.");

            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }

    /// <summary>
    /// The partitions of each member of the ledger's group, the members in the order of their first partition. Null if Kafka
    /// does not answer: the report then goes without them.
    /// </summary>
    public async Task<IReadOnlyList<IReadOnlyList<int>>?> ReadLedgerAssignmentAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var group = (await kafka.DescribeConsumerGroupsAsync(
                [ledgerGroup], new DescribeConsumerGroupsOptions { RequestTimeout = KafkaTimeout })).ConsumerGroupDescriptions.Single();

            return group.Members
                .Select(member => (IReadOnlyList<int>)member.Assignment.TopicPartitions
                    .Where(partition => partition.Topic == Topics.PaymentsCaptured)
                    .Select(partition => partition.Partition.Value)
                    .Order()
                    .ToList())
                .Where(partitions => partitions.Count > 0)
                .OrderBy(partitions => partitions[0])
                .ToList();
        }
        catch (KafkaException e)
        {
            Console.WriteLine($"  The partitions of the ledgers are not known: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Waits until the stand has caught up with the load before: a run starts with an empty outbox and no lag.
    /// Returns the sample that showed it, the starting point of the next record.
    /// </summary>
    public async Task<BacklogSample> WaitCaughtUpAsync(CancellationToken ct) =>
        (await RecordAsync(Task.CompletedTask, csvPath: null, start: null, ct))[^1];

    /// <summary>
    /// Samples while the load runs and then until the stand has caught up with it: the outbox is empty and the ledger
    /// has committed everything in Kafka. The record begins with <paramref name="start"/>, taken before the load,
    /// so that it holds the start even if the first sample under load is late. Returns at once if the load fails;
    /// the caller gets the error from its task.
    /// </summary>
    public async Task<IReadOnlyList<BacklogSample>> RecordAsync(Task load, string? csvPath, BacklogSample? start, CancellationToken ct)
    {
        await using var csv = csvPath is null ? null : File.CreateText(csvPath);

        if (csv is not null)
            await csv.WriteLineAsync("time,outbox_rows,outbox_age_s," + string.Join(",", Partitions.Select(p => $"lag_{p.Partition.Value}")) + ",postgres_ms,kafka_ms");

        var samples = new List<BacklogSample>();

        async Task AddAsync(BacklogSample sample)
        {
            samples.Add(sample);
            if (csv is not null)
                await csv.WriteLineAsync(sample.ToCsv());
        }

        if (start is not null)
            await AddAsync(start);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        Stopwatch? sinceLoad = null;

        do
        {
            // Looked at before the sample, so that a sample that has caught up is taken after the load.
            var loadOver = load.IsCompleted;
            if (loadOver && !load.IsCompletedSuccessfully)
                break;

            if (loadOver)
                sinceLoad ??= Stopwatch.StartNew();

            ensureStand();

            if (await TrySampleAsync(ct) is { } sample)
            {
                await AddAsync(sample);
                if (samples.Count % 10 == 0)
                    Console.WriteLine($"  {sample}");

                if (loadOver && sample.CaughtUp)
                    break;
            }

            if (sinceLoad?.Elapsed > CatchUpTimeout)
                throw new MatrixException($"The stand has not caught up in {CatchUpTimeout.TotalMinutes:0} min. Last sample: {samples.LastOrDefault()}");
        }
        while (await timer.WaitForNextTickAsync(ct));

        return samples;
    }

    // A sample that fails, say Kafka slow to answer under load, is skipped: a second missing from the record
    // is better than a run thrown away. So is a slow one: it read the ends of the partitions long after the commits,
    // so its lag counts every message that came in meanwhile (16 063 in a sample of 66 s).
    private async Task<BacklogSample?> TrySampleAsync(CancellationToken ct)
    {
        BacklogSample sample;
        try
        {
            sample = await SampleAsync(ct);
        }
        catch (Exception e) when (e is KafkaException or NpgsqlException)
        {
            Console.WriteLine($"  Backlog sample failed: {e.Message}");
            return null;
        }

        if (sample.Postgres <= SlowSample && sample.Kafka <= SlowSample)
            return sample;

        Console.WriteLine($"  Slow sample left out: Postgres {sample.Postgres.TotalSeconds:0.0} s, Kafka {sample.Kafka.TotalSeconds:0.0} s");
        return null;
    }

    // Postgres and Kafka at once: one that is slow to answer does not hold up the other's reading.
    private async Task<BacklogSample> SampleAsync(CancellationToken ct)
    {
        var at = DateTimeOffset.Now;

        var outbox = ReadOutboxAsync(ct);
        var lag = ReadLagAsync(ct);

        await Task.WhenAll(outbox, lag);

        var (rows, age, postgres) = outbox.Result;
        var (ends, lags, kafkaTook) = lag.Result;

        return new BacklogSample(at, rows, age, ends, lags, postgres, kafkaTook);
    }

    private async Task<(long Rows, double AgeSeconds, TimeSpan Took)> ReadOutboxAsync(CancellationToken ct)
    {
        var took = Stopwatch.StartNew();
        await using var command = payments.CreateCommand(
            "SELECT count(*), coalesce(extract(epoch FROM now() - min(created_at)), 0)::float8 FROM outbox");
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (reader.GetInt64(0), reader.GetDouble(1), took.Elapsed);
    }

    private async Task<(long[] EndOffsets, long[] Lag, TimeSpan Took)> ReadLagAsync(CancellationToken ct)
    {
        var took = Stopwatch.StartNew();

        // The commits first, then the ends: an end read later is never below a commit read earlier, so the lag is never
        // negative. At worst it counts the few messages that came in between the two calls.
        var commits = await kafka.ListConsumerGroupOffsetsAsync(
            [new ConsumerGroupTopicPartitions(ledgerGroup, Partitions)],
            new ListConsumerGroupOffsetsOptions { RequestTimeout = KafkaTimeout });

        var committed = commits.Single().Partitions.ToDictionary(
            commit => commit.Partition.Value,
            commit => Math.Max(0, commit.Offset.Value)); // a partition with no commit yet has Offset.Unset, below zero

        var ends = await kafka.ListOffsetsAsync(
            Partitions.Select(partition => new TopicPartitionOffsetSpec { TopicPartition = partition, OffsetSpec = OffsetSpec.Latest() }),
            new ListOffsetsOptions { RequestTimeout = KafkaTimeout });

        var end = ends.ResultInfos.ToDictionary(
            info => info.TopicPartitionOffsetError.Partition.Value,
            info => info.TopicPartitionOffsetError.Offset.Value);

        return (
            Partitions.Select(p => end[p.Partition.Value]).ToArray(),
            Partitions.Select(p => end[p.Partition.Value] - committed[p.Partition.Value]).ToArray(),
            took.Elapsed);
    }
}

/// <summary>
/// One second of the backlog: rows in the outbox and the age of the oldest, the end offset and the lag of every partition,
/// and how long Postgres and Kafka took to tell.
/// </summary>
public sealed record BacklogSample(
    DateTimeOffset At, long OutboxRows, double OutboxAgeSeconds, long[] EndOffsets, long[] Lag, TimeSpan Postgres, TimeSpan Kafka)
{
    public bool CaughtUp => OutboxRows == 0 && Lag.All(lag => lag == 0);

    /// <summary>How far the ledger has committed the partition: its end less its lag.</summary>
    public long Committed(int partition) => EndOffsets[partition] - Lag[partition];

    public string ToCsv() =>
        $"{At:HH:mm:ss},{OutboxRows},{OutboxAgeSeconds:0.000},{string.Join(",", Lag)},{Postgres.TotalMilliseconds:0},{Kafka.TotalMilliseconds:0}";

    public override string ToString() =>
        $"{At:HH:mm:ss}  outbox {OutboxRows} rows, the oldest {OutboxAgeSeconds:0.0} s;  lag by partition {string.Join(" ", Lag)}";
}

/// <summary>
/// The backlog of one run, from its samples. PeakWrite is the fastest the ledger wrote each partition, PeakWriteTotal all
/// of them together: the most its commits moved in <see cref="PeakWindow"/> of samples, a second. While a partition lags,
/// that is as fast as its ledger writes it; while it does not, it is only what came in.
/// </summary>
public sealed record BacklogSummary(
    double OutboxMaxAgeSeconds, long[] LagMax, long[] Messages, double CaughtUpSeconds, double LongestGapSeconds,
    double[] PeakWrite, double PeakWriteTotal)
{
    // Long enough that a batch of 500 committed at once does not show as a rate of thousands a second.
    public static readonly TimeSpan PeakWindow = TimeSpan.FromSeconds(10);

    public static BacklogSummary Of(IReadOnlyList<BacklogSample> samples, DateTimeOffset loadFinished)
    {
        var partitions = Enumerable.Range(0, samples[0].Lag.Length).ToList();
        return new BacklogSummary(
            OutboxMaxAgeSeconds: samples.Max(sample => sample.OutboxAgeSeconds),
            LagMax: partitions.Select(p => samples.Max(sample => sample.Lag[p])).ToArray(),
            // What came into each partition from the first sample to the last: its end offset moved by that much.
            Messages: partitions.Select(p => samples[^1].EndOffsets[p] - samples[0].EndOffsets[p]).ToArray(),
            // The record ends with the first sample that has caught up after the load.
            CaughtUpSeconds: Math.Max(0, (samples[^1].At - loadFinished).TotalSeconds),
            // A second between samples, or longer where one failed or was slow: in that time the maxima above were not looked at.
            LongestGapSeconds: samples.Zip(samples.Skip(1), (before, after) => (after.At - before.At).TotalSeconds).DefaultIfEmpty(0).Max(),
            PeakWrite: partitions.Select(p => PeakPerSecond(samples, sample => sample.Committed(p))).ToArray(),
            PeakWriteTotal: PeakPerSecond(samples, sample => partitions.Sum(sample.Committed)));
    }

    // Every window from a sample to the first one at least PeakWindow after it; 0 if the record is shorter.
    private static double PeakPerSecond(IReadOnlyList<BacklogSample> samples, Func<BacklogSample, long> committed)
    {
        var peak = 0.0;
        var to = 0;
        for (var from = 0; from < samples.Count; from++)
        {
            while (to < samples.Count && samples[to].At - samples[from].At < PeakWindow)
                to++;

            if (to == samples.Count)
                break;

            var seconds = (samples[to].At - samples[from].At).TotalSeconds;
            peak = Math.Max(peak, (committed(samples[to]) - committed(samples[from])) / seconds);
        }
        return peak;
    }
}
