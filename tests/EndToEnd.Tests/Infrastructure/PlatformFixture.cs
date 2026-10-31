using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Contracts;
using Ledger.Service.Ledger;
using Npgsql;
using Payments.Api.Telemetry;
using Testcontainers.Kafka;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace EndToEnd.Tests.Infrastructure;

/// <summary>
/// The whole platform in one test process: Postgres, Kafka and Redis in containers, Payments.Api and Ledger.Service
/// hosted in-process. The services get their settings through configuration, as in production; only the bank
/// is the built-in stand-in (Acquirer:Mode=Fake), so neither the network nor the Go emulator is needed.
/// Ledger.Service can be stopped and started again in the middle of a test.
/// </summary>
public sealed class PlatformFixture : IAsyncLifetime
{
    // Set here rather than taken from appsettings.json: the offsets the tests read and move are the ledger's for sure.
    private const string LedgerGroup = "ledger";

    private static readonly TopicPartition[] CapturedPartitions = Enumerable.Range(0, Topics.PaymentsCapturedPartitions)
        .Select(partition => new TopicPartition(Topics.PaymentsCaptured, partition))
        .ToArray();

    // The same images as in docker-compose.yml.
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly KafkaContainer kafka = new KafkaBuilder("apache/kafka:4.0.0").Build();
    private readonly RedisContainer redis = new RedisBuilder("redis:7.4-alpine").Build();

    private ServiceFactory<PaymentsMetrics> paymentsApi = null!;
    private ServiceFactory<LedgerQueries> ledgerService = null!;
    private Dictionary<string, string?> ledgerSettings = null!;
    private IAdminClient kafkaAdmin = null!;

    /// <summary>HTTP client of Payments.Api.</summary>
    public HttpClient Payments { get; private set; } = null!;

    /// <summary>HTTP client of Ledger.Service. Every <see cref="StartLedger"/> replaces it.</summary>
    public HttpClient Ledger { get; private set; } = null!;

    /// <summary>The services' databases, for the checks their APIs do not offer.</summary>
    public NpgsqlDataSource PaymentsDb { get; private set; } = null!;

    public NpgsqlDataSource LedgerDb { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(postgres.StartAsync(), kafka.StartAsync(), redis.StartAsync());

        // Each service owns its database, as in db/create-databases.sql. One command per database:
        // CREATE DATABASE cannot run inside the implicit transaction of a multi-statement command.
        await using (var server = NpgsqlDataSource.Create(postgres.GetConnectionString()))
        {
            foreach (var database in new[] { "payments", "ledger" })
            {
                await using var command = server.CreateCommand($"CREATE DATABASE {database}");
                await command.ExecuteNonQueryAsync();
            }
        }

        PaymentsDb = NpgsqlDataSource.Create(ConnectionString("payments"));
        LedgerDb = NpgsqlDataSource.Create(ConnectionString("ledger"));

        // host:port, as in appsettings.json; GetBootstrapAddress() returns it as a PLAINTEXT:// URI.
        var kafkaAddress = new Uri(kafka.GetBootstrapAddress()).Authority;

        paymentsApi = new ServiceFactory<PaymentsMetrics>(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Payments"] = ConnectionString("payments"),
            ["ConnectionStrings:Kafka"] = kafkaAddress,
            ["ConnectionStrings:Redis"] = redis.GetConnectionString(),
            ["Acquirer:Mode"] = "Fake",
            ["Acquirer:LatencyMs"] = "20",                   // copies of a payment arrive while the first is still at the bank
            ["Acquirer:DeclineRatePercent"] = "10",          // both outcomes in the mix
            ["RateLimiting:Merchant:Enabled"] = "false",     // the test sends a merchant far more than its limit
            ["Logging:LogLevel:Default"] = "Warning",
            // Nothing collects telemetry here. With the SDK on, every stop of a service waits up to 5 s per signal
            // for the exporters to give up on localhost:4317, and on Windows a refused connection alone takes seconds.
            ["OTEL_SDK_DISABLED"] = "true",
        });

        ledgerSettings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Ledger"] = ConnectionString("ledger"),
            ["ConnectionStrings:Kafka"] = kafkaAddress,
            ["Consumer:GroupId"] = LedgerGroup,
            ["Logging:LogLevel:Default"] = "Warning",
            ["OTEL_SDK_DISABLED"] = "true",
        };

        kafkaAdmin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = kafkaAddress }).Build();

        // CreateClient starts the service: its schema, the Kafka topic, the background jobs.
        Payments = paymentsApi.CreateClient();
        StartLedger();
    }

    /// <summary>
    /// Stops Ledger.Service the way a deployment does: the host shuts down, and the consumer leaves its group.
    /// Batches it has read but not written yet are dropped, and their offsets are not committed.
    /// Payments.Api goes on working: its events wait in Kafka.
    /// </summary>
    public async Task StopLedgerAsync() => await ledgerService.DisposeAsync();

    /// <summary>
    /// Starts a new instance of Ledger.Service with the same settings. It joins the group and goes on from the committed offsets.
    /// </summary>
    public void StartLedger()
    {
        ledgerService = new ServiceFactory<LedgerQueries>(ledgerSettings);
        Ledger = ledgerService.CreateClient();
    }

    /// <summary>
    /// Moves the ledger's committed offsets back to the start of payments.captured: after a restart it reads the whole topic again.
    /// Only while the ledger is stopped: Kafka changes the offsets of a group only when the group has no members.
    /// </summary>
    public async Task RewindLedgerAsync() =>
        await kafkaAdmin.AlterConsumerGroupOffsetsAsync(
        [
            new ConsumerGroupTopicPartitionOffsets(LedgerGroup, CapturedPartitions.Select(p => new TopicPartitionOffset(p, 0)).ToList()),
        ]);

    /// <summary>
    /// How many messages of payments.captured the ledger has not committed yet. It commits a batch only after writing it,
    /// so zero means that everything in Kafka is in the ledger's database.
    /// </summary>
    public async Task<long> LedgerLagAsync()
    {
        var ends = await kafkaAdmin.ListOffsetsAsync(
            CapturedPartitions.Select(p => new TopicPartitionOffsetSpec { TopicPartition = p, OffsetSpec = OffsetSpec.Latest() }));

        var committed = (await kafkaAdmin.ListConsumerGroupOffsetsAsync([new ConsumerGroupTopicPartitions(LedgerGroup, CapturedPartitions.ToList())]))
            .Single().Partitions
            .ToDictionary(p => p.TopicPartition, p => p.Offset.Value);

        // A partition with no commit yet has a negative offset (Offset.Unset): the ledger will read it from the start.
        return ends.ResultInfos.Sum(end =>
            end.TopicPartitionOffsetError.Offset.Value - Math.Max(0, committed[end.TopicPartitionOffsetError.TopicPartition]));
    }

    public async ValueTask DisposeAsync()
    {
        // Producer side first, then the consumer, then what they both use. A test may have stopped the ledger already:
        // disposing a factory twice is harmless.
        await paymentsApi.DisposeAsync();
        await ledgerService.DisposeAsync();
        kafkaAdmin.Dispose();
        await PaymentsDb.DisposeAsync();
        await LedgerDb.DisposeAsync();
        await Task.WhenAll(postgres.DisposeAsync().AsTask(), kafka.DisposeAsync().AsTask(), redis.DisposeAsync().AsTask());
    }

    private string ConnectionString(string database) =>
        new NpgsqlConnectionStringBuilder(postgres.GetConnectionString()) { Database = database }.ConnectionString;
}
