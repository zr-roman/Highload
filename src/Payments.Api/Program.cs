using Confluent.Kafka;
using Microsoft.Extensions.Http.Resilience;
using Npgsql;
using Payments.Api.Acquiring;
using Payments.Api.Antifraud;
using Payments.Api.Database;
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

var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
if (corsOrigins.Length > 0)
    builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
        .WithOrigins(corsOrigins)
        .WithMethods("GET", "POST")
        .WithHeaders("Content-Type", "Idempotency-Key")
        .WithExposedHeaders("Retry-After")));

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

// Validated by MerchantQuotas, not here: see its remarks.
builder.Services.AddOptions<MerchantRateLimitOptions>()
    .Bind(builder.Configuration.GetSection("RateLimiting:Merchant"));

builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(postgres));

// The leader locks on their own connections straight to the primary, when the main ones go through PgBouncer
// (LeaderDataSource). One per job, held for as long as the replica leads: a small pool of its own.
if (builder.Configuration.GetConnectionString("PaymentsLeader") is { Length: > 0 } leader)
    builder.Services.AddSingleton(_ => new LeaderDataSource(NpgsqlDataSource.Create(leader)));
builder.Services.AddSingleton<IProducer<string, string>>(_ => new ProducerBuilder<string, string>(new ProducerConfig
{
    BootstrapServers = kafka,
    Acks = Acks.All,            // the broker confirms the write before the outbox row is deleted
    EnableIdempotence = true,   // producer retries cause neither duplicates nor reordering
    LingerMs = 5,               // let the client collect messages into batches for up to 5 ms
    // How long a request to a broker may stay unanswered. Default 60 s: when the network to a broker's node broke,
    // its in-flight produce requests hung for all of it, and the idempotent producer sends nothing more to those
    // partitions until they are settled, so the outbox stood still for a minute after Kafka had new leaders
    // (docs/kafka-failover.md). Inside the cluster an answer takes milliseconds; a request cancelled after 10 s is
    // sent again to the new leader, and idempotence keeps the retry from writing twice.
    SocketTimeoutMs = 10_000,
}).Build());

builder.Services.Configure<FeeOptions>(builder.Configuration.GetSection("Fees"));
builder.Services.Configure<AcquirerOptions>(builder.Configuration.GetSection("Acquirer"));
builder.Services.Configure<OutboxOptions>(builder.Configuration.GetSection("Outbox"));

builder.Services.AddSingleton<PaymentStore>();
builder.Services.AddSingleton<PaymentCompletion>();
builder.Services.AddSingleton<PaymentsMetrics>();
builder.Services.AddSingleton<IMerchantRateLimitStore, RedisTokenBucket>();
builder.Services.AddSingleton<RedisCircuitBreaker>();
builder.Services.AddSingleton<LocalMerchantLimiter>();
builder.Services.AddSingleton<MerchantQuotas>();
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddHostedService<OutboxRelay>();
builder.Services.AddHostedService<PaymentRecovery>();
// VALIDATE CONSTRAINT of the stage 4 migration, after the start: see SchemaAfterStart.
builder.Services.AddHostedService<SchemaAfterStart>();

var acquirer = builder.Configuration.GetSection("Acquirer").Get<AcquirerOptions>() ?? new AcquirerOptions();
if (acquirer.Mode == AcquirerMode.Http)
    builder.Services.AddHttpAcquirer(builder.Configuration.GetSection("Acquirer"));
else
    builder.Services.AddSingleton<IAcquirer, FakeAcquirer>();

// Registered, not called yet: the saga that checks a payment after the bank comes with stage 4, task 5.
var antifraud = builder.Configuration.GetSection("Antifraud").Get<AntifraudOptions>() ?? new AntifraudOptions();
if (antifraud.Mode == AntifraudMode.Http)
    builder.Services.AddHttpAntifraud(builder.Configuration.GetSection("Antifraud"));
else
    builder.Services.AddSingleton<IAntifraud, FakeAntifraud>();

builder.Services.AddHealthChecks()
    .AddCheck<PostgresHealthCheck>("postgres", tags: [HealthCheckEndpoints.ReadyTag], timeout: TimeSpan.FromSeconds(2));

var app = builder.Build();
if (corsOrigins.Length > 0)
    app.UseCors();

// From the content root, like appsettings.json. Not from AppContext.BaseDirectory: when the end-to-end tests host
// both services in one process, that is the tests' folder, and both services' schema.sql would land on one path there.
// Postgres may be out of reach for a moment while a pod starts: see StartupRetry.
// No Kafka here: payments are taken without it and wait in the outbox, so a pod must start while Kafka is down.
// The topic is created by the OutboxRelay before its first message.
// schema-after.sql, which may scan all of payments, runs after the start (SchemaAfterStart), not here.
await StartupRetry.RunAsync("Database schema", ct => DatabaseSchema.ApplyAsync(
    app.Services.GetRequiredService<NpgsqlDataSource>(),
    Path.Combine(app.Environment.ContentRootPath, "Database", "schema.sql"), ct), app.Logger);

// Connect at startup, not on the first request that needs Redis: the first attempt may take up to connectTimeout.
app.Services.GetRequiredService<IConnectionMultiplexer>();

// Merchant limits that do not validate fail the start, not the first payment.
app.Services.GetRequiredService<MerchantQuotas>();

// Every counter at zero from the first export on, on every instance: see PaymentsMetrics.StartCountersAtZero.
// Once the host has started: the OpenTelemetry SDK begins to listen to the meters with the host, and zeros recorded
// before that go nowhere.
app.Lifetime.ApplicationStarted.Register(app.Services.GetRequiredService<PaymentsMetrics>().StartCountersAtZero);

app.MapPaymentEndpoints();

app.MapPlatformHealthChecks();

app.Run();
