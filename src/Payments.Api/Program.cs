using Confluent.Kafka;
using Contracts;
using Microsoft.Extensions.Http.Resilience;
using Npgsql;
using Payments.Api.Acquiring;
using Payments.Api.Outbox;
using Payments.Api.Payments;
using Payments.Api.RateLimiting;
using Payments.Api.Recovery;
using Payments.Api.Telemetry;
using Platform;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
builder.AddPlatformTelemetry("payments-api", PaymentsMetrics.MeterName);
builder.AddPlatformRedis(keyPrefix: "payments:");

var postgres = builder.Configuration.GetConnectionString("Payments")
    ?? throw new InvalidOperationException("Connection string 'Payments' is not configured.");
var kafka = builder.Configuration.GetConnectionString("Kafka")
    ?? throw new InvalidOperationException("Connection string 'Kafka' is not configured.");

var httpStandardResilienceOptions = builder.Configuration.GetSection("Acquirer:Resilience").Get<HttpStandardResilienceOptions>() 
    ?? new HttpStandardResilienceOptions();

var msg = $"StaleAfter must be more than twice Acquirer:Resilience:TotalRequestTimeout ({httpStandardResilienceOptions.TotalRequestTimeout.Timeout})";

builder.Services.AddOptions<RecoveryOptions>()
    .Bind(builder.Configuration.GetSection("Recovery"))
    .Validate(o => o.Interval > TimeSpan.Zero && o.BatchSize > 0 && o.MaxParallelism > 0,
        "Recovery: Interval, BatchSize and MaxParallelism must be positive")
    .Validate(recoveryOptions => recoveryOptions.StaleAfter > 2 * httpStandardResilienceOptions.TotalRequestTimeout.Timeout, msg)
    .ValidateOnStart();

builder.Services.AddOptions<MerchantRateLimitOptions>()
    .Bind(builder.Configuration.GetSection("RateLimiting:Merchant"))
    .Validate(o => o.TokensPerSecond > 0 && o.Burst > 0, "RateLimiting: TokensPerSecond and Burst must be positive")
    .ValidateOnStart();

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

builder.Services.AddSingleton<PaymentStore>();
builder.Services.AddSingleton<PaymentCompletion>();
builder.Services.AddSingleton<PaymentsMetrics>();
builder.Services.AddSingleton<RedisTokenBucket>();
builder.Services.AddSingleton<RedisCircuitBreaker>();
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddHostedService<OutboxRelay>();
builder.Services.AddHostedService<PaymentRecovery>();

var acquirer = builder.Configuration.GetSection("Acquirer").Get<AcquirerOptions>() ?? new AcquirerOptions();
if (acquirer.Mode == AcquirerMode.Http)
    builder.Services.AddHttpAcquirer(builder.Configuration.GetSection("Acquirer"));
else
    builder.Services.AddSingleton<IAcquirer, FakeAcquirer>();

var app = builder.Build();

await DatabaseSchema.ApplyAsync(
    app.Services.GetRequiredService<NpgsqlDataSource>(),
    Path.Combine(AppContext.BaseDirectory, "Database", "schema.sql"));

await KafkaTopics.EnsureCreatedAsync(kafka, Topics.PaymentsCaptured, Topics.PaymentsCapturedPartitions, app.Logger);

// Connect at startup, not on the first request that needs Redis: the first attempt may take up to connectTimeout.
app.Services.GetRequiredService<IConnectionMultiplexer>();

app.MapPaymentEndpoints();
app.MapHealthChecks("/health");

app.Run();
