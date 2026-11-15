using Contracts;
using Ledger.Service.Consuming;
using Ledger.Service.Ledger;
using Ledger.Service.Telemetry;
using Npgsql;
using Platform;

var builder = WebApplication.CreateBuilder(args);
builder.AddPlatformTelemetry("ledger-service", LedgerMetrics.MeterName);

var postgres = builder.Configuration.GetConnectionString("Ledger")
    ?? throw new InvalidOperationException("Connection string 'Ledger' is not configured.");
var kafka = builder.Configuration.GetConnectionString("Kafka")
    ?? throw new InvalidOperationException("Connection string 'Kafka' is not configured.");

builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(postgres));
builder.Services.Configure<ConsumerOptions>(builder.Configuration.GetSection("Consumer"));
builder.Services.AddSingleton<LedgerWriter>();
builder.Services.AddSingleton<LedgerQueries>();
builder.Services.AddSingleton<LedgerMetrics>();
builder.Services.AddHostedService<PaymentsConsumer>();

builder.Services.AddHealthChecks()
    .AddCheck<PostgresHealthCheck>("postgres", tags: [HealthCheckEndpoints.ReadyTag], timeout: TimeSpan.FromSeconds(2));

var app = builder.Build();

// From the content root, like appsettings.json. Not from AppContext.BaseDirectory: when the end-to-end tests host
// both services in one process, that is the tests' folder, and both services' schema.sql would land on one path there.
// Postgres and Kafka may be out of reach for a moment while a pod starts: see StartupRetry.
await StartupRetry.RunAsync("Database schema", ct => DatabaseSchema.ApplyAsync(
    app.Services.GetRequiredService<NpgsqlDataSource>(),
    Path.Combine(app.Environment.ContentRootPath, "Database", "schema.sql"), ct), app.Logger);
await StartupRetry.RunAsync("Kafka topic", _ => KafkaTopics.EnsureCreatedAsync(
    kafka, Topics.PaymentsCaptured, Topics.PaymentsCapturedPartitions, app.Logger), app.Logger);

// Every counter at zero from the first export on: see LedgerMetrics.StartCountersAtZero. Once the host has started:
// the OpenTelemetry SDK begins to listen to the meters with the host, and zeros recorded before that go nowhere.
app.Lifetime.ApplicationStarted.Register(app.Services.GetRequiredService<LedgerMetrics>().StartCountersAtZero);

app.MapGet("/balances/merchants/{merchantId:long}", async (long merchantId, LedgerQueries queries, CancellationToken ct) =>
    await queries.GetBalanceAsync(LedgerAccounts.MerchantAccounts(merchantId), ct) is { } balance
        ? Results.Ok(new { merchantId, balance })
        : Results.NotFound());

app.MapGet("/balances/platform-fees", async (LedgerQueries queries, CancellationToken ct) =>
    Results.Ok(new { balance = await queries.GetPlatformFeesAsync(ct) }));

// Heavy on a large ledger: for demos and chaos tests, not for production traffic.
app.MapGet("/invariants", async (LedgerQueries queries, CancellationToken ct) =>
    Results.Ok(await queries.CheckInvariantsAsync(ct)));

app.MapPlatformHealthChecks();

app.Run();
