using Contracts;
using History.Service.Consuming;
using History.Service.History;
using History.Service.Telemetry;
using MongoDB.Driver;
using Platform;

var builder = WebApplication.CreateBuilder(args);
builder.AddPlatformTelemetry("history-service", HistoryMetrics.MeterName);

var mongo = builder.Configuration.GetConnectionString("History")
    ?? throw new InvalidOperationException("Connection string 'History' is not configured.");

var kafka = builder.Configuration.GetConnectionString("Kafka")
    ?? throw new InvalidOperationException("Connection string 'Kafka' is not configured.");

// One client for the process: it holds the pool of connections and watches the server.
builder.Services.AddSingleton<IMongoClient>(_ => new MongoClient(mongo));
builder.Services.Configure<HistoryOptions>(builder.Configuration.GetSection("History"));
builder.Services.Configure<ConsumerOptions>(builder.Configuration.GetSection("Consumer"));
builder.Services.AddSingleton<HistoryStore>();
builder.Services.AddSingleton<HistoryMetrics>();
builder.Services.AddHostedService<HistoryConsumer>();

builder.Services.AddHealthChecks()
    .AddCheck<MongoHealthCheck>("mongodb", tags: [HealthCheckEndpoints.ReadyTag], timeout: TimeSpan.FromSeconds(2));

var app = builder.Build();

// MongoDB and Kafka may be out of reach for a moment while a pod starts: see StartupRetry. The topics are those of
// Payments.Api; created here as well, so that the consumer does not subscribe to topics that are not there yet.
await StartupRetry.RunAsync("History indexes", ct =>
    app.Services.GetRequiredService<HistoryStore>().EnsureIndexesAsync(ct), app.Logger);

await StartupRetry.RunAsync("Kafka topics", _ => KafkaTopics.EnsureCreatedAsync(kafka,
    [(Topics.PaymentsCaptured, Topics.PaymentsCapturedPartitions), (Topics.PaymentsFinalized, Topics.PaymentsFinalizedPartitions)],
    app.Logger), app.Logger);

// Every counter at zero from the first export on: see HistoryMetrics.StartCountersAtZero.
app.Lifetime.ApplicationStarted.Register(app.Services.GetRequiredService<HistoryMetrics>().StartCountersAtZero);

app.MapPlatformHealthChecks();

app.Run();
