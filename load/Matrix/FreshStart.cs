using System.Diagnostics;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Contracts;
using Npgsql;

namespace Matrix;

/// <summary>
/// --fresh: the stand from scratch, so that every matrix starts on the same empty databases. The check of every posting
/// by /invariants and the search of PaymentRecovery through all payments get slower as the tables grow: with millions
/// of rows left from earlier runs, the repeats of one matrix and the next measure different stands.
/// </summary>
public static class FreshStart
{
    private static readonly TimeSpan KafkaTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Runs before the stand starts: no service holds the tables, the topic or the group.</summary>
    public static async Task EmptyAsync(StandSettings settings, IAdminClient kafka, CancellationToken ct)
    {
        // Kafka first: a message left in the topic would reach an empty ledger and break its balances, and if Kafka
        // fails here, the databases are still whole.
        await DeleteGroupAsync(kafka, settings.LedgerGroup, ct);
        await DeleteTopicAsync(kafka, Topics.PaymentsCaptured, ct);

        foreach (var database in new[] { settings.PaymentsDatabase, settings.LedgerDatabase })
            await TruncateAsync(database, ct);
    }

    // A ledger the last matrix killed did not leave the group: Kafka keeps it a member until its session times out,
    // 45 s by default. A member still there after a minute is a ledger that runs.
    private static async Task DeleteGroupAsync(IAdminClient kafka, string group, CancellationToken ct)
    {
        var waited = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                await kafka.DeleteGroupsAsync([group], new DeleteGroupsOptions { RequestTimeout = KafkaTimeout });
                Console.WriteLine($"  Kafka: consumer group {group} deleted");
                return;
            }
            catch (DeleteGroupsException e) when (e.Results.All(result => result.Error.Code == ErrorCode.GroupIdNotFound))
            {
                return; // a Kafka created anew has no group yet
            }
            catch (DeleteGroupsException e) when (e.Results.Any(result => result.Error.Code == ErrorCode.NonEmptyGroup))
            {
                if (waited.Elapsed > TimeSpan.FromMinutes(1))
                    throw new MatrixException($"Consumer group {group} still has members after a minute: a ledger runs somewhere else. Stop it and start again.");

                if (waited.Elapsed < TimeSpan.FromSeconds(2))
                    Console.WriteLine($"  Kafka: consumer group {group} has members, waiting for the ledger of the last stand to time out");
            }
            catch (DeleteGroupsException e) when (e.Results.Any(result => result.Error.Code is
                ErrorCode.NotCoordinatorForGroup or ErrorCode.GroupCoordinatorNotAvailable or ErrorCode.GroupLoadInProgress))
            {
                // The coordinator of the group is moving or loading, as just after a stand was killed or Kafka restarted.
                if (waited.Elapsed > TimeSpan.FromMinutes(1))
                    throw new MatrixException($"Kafka could not delete the consumer group {group} in a minute: {e.Results[0].Error.Reason}");
            }
            catch (DeleteGroupsException e)
            {
                throw new MatrixException($"Kafka could not delete the consumer group {group}: {e.Results[0].Error.Reason}");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    private static async Task DeleteTopicAsync(IAdminClient kafka, string topic, CancellationToken ct)
    {
        try
        {
            await kafka.DeleteTopicsAsync([topic], new DeleteTopicsOptions { RequestTimeout = KafkaTimeout, OperationTimeout = KafkaTimeout });
        }
        catch (DeleteTopicsException e) when (e.Results.All(result => result.Error.Code == ErrorCode.UnknownTopicOrPart))
        {
            return; // nothing to delete
        }
        catch (DeleteTopicsException e)
        {
            throw new MatrixException($"Kafka could not delete the topic {topic}: {e.Results[0].Error.Reason}");
        }

        // Kafka deletes a topic in the background. The services create it anew at their start (KafkaTopics),
        // and a topic that is still being deleted would fail that.
        var waited = Stopwatch.StartNew();
        while (kafka.GetMetadata(topic, KafkaTimeout).Topics.Any(t => t.Topic == topic && t.Error.Code != ErrorCode.UnknownTopicOrPart))
        {
            if (waited.Elapsed > TimeSpan.FromSeconds(30))
                throw new MatrixException($"Kafka has not deleted the topic {topic} in 30 s.");

            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }
        Console.WriteLine($"  Kafka: topic {topic} deleted");
    }

    // Every table of the service, emptied: the schema stays, and the service needs nothing else at its start.
    // TRUNCATE also gives the disk back at once, where DELETE would leave the rows to vacuum.
    private static async Task TruncateAsync(string connectionString, CancellationToken ct)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync(ct);

        var tables = new List<string>();

        await using (var list = new NpgsqlCommand("SELECT quote_ident(tablename) FROM pg_tables WHERE schemaname = 'public' ORDER BY tablename", connection))
        await using (var reader = await list.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                tables.Add(reader.GetString(0));

        var database = new NpgsqlConnectionStringBuilder(connectionString).Database;
        if (tables.Count == 0)
        {
            Console.WriteLine($"  Postgres: {database} has no tables yet");
            return;
        }

        await using (var truncate = new NpgsqlCommand($"TRUNCATE {string.Join(", ", tables)} RESTART IDENTITY", connection))
            await truncate.ExecuteNonQueryAsync(ct);

        Console.WriteLine($"  Postgres: {database} emptied ({string.Join(", ", tables)})");
    }
}
