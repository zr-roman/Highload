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

    /// <summary>Waits until the stand has caught up with the load before: a run starts with an empty outbox and no lag.</summary>
    public async Task WaitCaughtUpAsync(CancellationToken ct) => await RecordAsync(Task.CompletedTask, csvPath: null, ct);

    /// <summary>
    /// Samples while the load runs and then until the stand has caught up with it: the outbox is empty and the ledger
    /// has committed everything in Kafka. Returns at once if the load fails; the caller gets the error from its task.
    /// </summary>
    public async Task<IReadOnlyList<BacklogSample>> RecordAsync(Task load, string? csvPath, CancellationToken ct)
    {
        await using var csv = csvPath is null ? null : File.CreateText(csvPath);

        if (csv is not null)
            await csv.WriteLineAsync("time,outbox_rows,outbox_age_s," + string.Join(",", Partitions.Select(p => $"lag_{p.Partition.Value}")));

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        Stopwatch? sinceLoad = null;

        var samples = new List<BacklogSample>();

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
                samples.Add(sample);
                if (csv is not null)
                    await csv.WriteLineAsync(sample.ToCsv());

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
    // is better than a run thrown away.
    private async Task<BacklogSample?> TrySampleAsync(CancellationToken ct)
    {
        try
        {
            return await SampleAsync(ct);
        }
        catch (Exception e) when (e is KafkaException or NpgsqlException)
        {
            Console.WriteLine($"  Backlog sample failed: {e.Message}");
            return null;
        }
    }

    private async Task<BacklogSample> SampleAsync(CancellationToken ct)
    {
        await using var command = payments.CreateCommand(
            "SELECT count(*), coalesce(extract(epoch FROM now() - min(created_at)), 0)::float8 FROM outbox");
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var outboxRows = reader.GetInt64(0);
        var outboxAge = reader.GetDouble(1);

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

        return new BacklogSample(
            DateTimeOffset.Now,
            outboxRows,
            outboxAge,
            Partitions.Select(p => end[p.Partition.Value]).ToArray(),
            Partitions.Select(p => end[p.Partition.Value] - committed[p.Partition.Value]).ToArray());
    }
}

/// <summary>One second of the backlog: rows in the outbox and the age of the oldest, the end offset and the lag of every partition.</summary>
public sealed record BacklogSample(DateTimeOffset At, long OutboxRows, double OutboxAgeSeconds, long[] EndOffsets, long[] Lag)
{
    public bool CaughtUp => OutboxRows == 0 && Lag.All(lag => lag == 0);

    public string ToCsv() => $"{At:HH:mm:ss},{OutboxRows},{OutboxAgeSeconds:0.000},{string.Join(",", Lag)}";

    public override string ToString() =>
        $"{At:HH:mm:ss}  outbox {OutboxRows} rows, the oldest {OutboxAgeSeconds:0.0} s;  lag by partition {string.Join(" ", Lag)}";
}

/// <summary>The backlog of one run, from its samples.</summary>
public sealed record BacklogSummary(double OutboxMaxAgeSeconds, long[] LagMax, long[] Messages, double CaughtUpSeconds)
{
    public static BacklogSummary Of(IReadOnlyList<BacklogSample> samples, DateTimeOffset loadFinished)
    {
        var partitions = Enumerable.Range(0, samples[0].Lag.Length).ToList();
        return new BacklogSummary(
            OutboxMaxAgeSeconds: samples.Max(sample => sample.OutboxAgeSeconds),
            LagMax: partitions.Select(p => samples.Max(sample => sample.Lag[p])).ToArray(),
            // What came into each partition from the first sample to the last: its end offset moved by that much.
            Messages: partitions.Select(p => samples[^1].EndOffsets[p] - samples[0].EndOffsets[p]).ToArray(),
            // The record ends with the first sample that has caught up after the load.
            CaughtUpSeconds: Math.Max(0, (samples[^1].At - loadFinished).TotalSeconds));
    }
}
