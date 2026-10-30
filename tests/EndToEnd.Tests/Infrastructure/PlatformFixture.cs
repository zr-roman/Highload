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
/// </summary>
public sealed class PlatformFixture : IAsyncLifetime
{
    // The same images as in docker-compose.yml.
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly KafkaContainer kafka = new KafkaBuilder("apache/kafka:4.0.0").Build();
    private readonly RedisContainer redis = new RedisBuilder("redis:7.4-alpine").Build();

    private ServiceFactory<PaymentsMetrics> paymentsApi = null!;
    private ServiceFactory<LedgerQueries> ledgerService = null!;

    /// <summary>HTTP client of Payments.Api.</summary>
    public HttpClient Payments { get; private set; } = null!;

    /// <summary>HTTP client of Ledger.Service.</summary>
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
        });

        ledgerService = new ServiceFactory<LedgerQueries>(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Ledger"] = ConnectionString("ledger"),
            ["ConnectionStrings:Kafka"] = kafkaAddress,
            ["Logging:LogLevel:Default"] = "Warning",
        });

        // CreateClient starts the service: its schema, the Kafka topic, the background jobs.
        Payments = paymentsApi.CreateClient();
        Ledger = ledgerService.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        // Producer side first, then the consumer, then what they both use.
        await paymentsApi.DisposeAsync();
        await ledgerService.DisposeAsync();
        await PaymentsDb.DisposeAsync();
        await LedgerDb.DisposeAsync();
        await Task.WhenAll(postgres.DisposeAsync().AsTask(), kafka.DisposeAsync().AsTask(), redis.DisposeAsync().AsTask());
    }

    private string ConnectionString(string database) =>
        new NpgsqlConnectionStringBuilder(postgres.GetConnectionString()) { Database = database }.ConnectionString;
}
