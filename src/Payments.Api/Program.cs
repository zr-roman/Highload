using Confluent.Kafka;
using Contracts;
using Npgsql;
using Payments.Api.Acquiring;
using Payments.Api.Outbox;
using Payments.Api.Payments;
using Payments.Api.Telemetry;
using Platform;

var builder = WebApplication.CreateBuilder(args);
builder.AddPlatformTelemetry("payments-api", PaymentsMetrics.MeterName);

var postgres = builder.Configuration.GetConnectionString("Payments")
    ?? throw new InvalidOperationException("Connection string 'Payments' is not configured.");
var kafka = builder.Configuration.GetConnectionString("Kafka")
    ?? throw new InvalidOperationException("Connection string 'Kafka' is not configured.");

builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(postgres));
builder.Services.AddSingleton<IProducer<string, string>>(_ => new ProducerBuilder<string, string>(new ProducerConfig
{
    BootstrapServers = kafka,
    Acks = Acks.All,            // the broker confirms the write before the outbox row is deleted
    EnableIdempotence = true,   // producer retries cause neither duplicates nor reordering
    LingerMs = 5,               // let the client collect messages into batches for up to 5 ms
}).Build());

builder.Services.Configure<FeeOptions>(builder.Configuration.GetSection("Fees"));
builder.Services.Configure<AcquirerOptions>(builder.Configuration.GetSection("Acquirer"));
builder.Services.Configure<OutboxOptions>(builder.Configuration.GetSection("Outbox"));
builder.Services.AddSingleton<IAcquirer, FakeAcquirer>();
builder.Services.AddSingleton<PaymentStore>();
builder.Services.AddSingleton<PaymentsMetrics>();
builder.Services.AddHostedService<OutboxRelay>();

var app = builder.Build();

await DatabaseSchema.ApplyAsync(
    app.Services.GetRequiredService<NpgsqlDataSource>(),
    Path.Combine(AppContext.BaseDirectory, "Database", "schema.sql"));
await KafkaTopics.EnsureCreatedAsync(kafka, Topics.PaymentsCaptured, Topics.PaymentsCapturedPartitions, app.Logger);

app.MapPaymentEndpoints();
app.MapHealthChecks("/health");

app.Run();
