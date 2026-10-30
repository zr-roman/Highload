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

var app = builder.Build();

// From the content root, like appsettings.json. Not from AppContext.BaseDirectory: when the end-to-end tests host
// both services in one process, that is the tests' folder, and both services' schema.sql would land on one path there.
await DatabaseSchema.ApplyAsync(
    app.Services.GetRequiredService<NpgsqlDataSource>(),
    Path.Combine(app.Environment.ContentRootPath, "Database", "schema.sql"));
await KafkaTopics.EnsureCreatedAsync(kafka, Topics.PaymentsCaptured, Topics.PaymentsCapturedPartitions, app.Logger);

app.MapGet("/balances/merchants/{merchantId:long}", async (long merchantId, LedgerQueries queries, CancellationToken ct) =>
    await queries.GetBalanceAsync(LedgerAccounts.Merchant(merchantId), ct) is { } balance
        ? Results.Ok(new { merchantId, balance })
        : Results.NotFound());

app.MapGet("/balances/platform-fees", async (LedgerQueries queries, CancellationToken ct) =>
    Results.Ok(new { balance = await queries.GetPlatformFeesAsync(ct) }));

// Heavy on a large ledger: for demos and chaos tests, not for production traffic.
app.MapGet("/invariants", async (LedgerQueries queries, CancellationToken ct) =>
    Results.Ok(await queries.CheckInvariantsAsync(ct)));

app.MapHealthChecks("/health");

app.Run();
