using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Payments.Api.Acquiring;
using Payments.Api.Payments;
using Payments.Api.Recovery;
using Payments.Api.Telemetry;

namespace Payments.Api.Tests.Infrastructure;

/// <summary>
/// PaymentRecovery assembled the way Program.cs does it, with the bank, the clock and logging replaced.
/// Every host has its own connection pool, like a separate process: disposing the host closes the pool,
/// and the session holding the leader's advisory lock goes with it.
/// </summary>
public sealed class RecoveryHost : IAsyncDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(30);

    private readonly NpgsqlDataSource dataSource;
    private readonly ServiceProvider services;
    private readonly PaymentRecovery recovery;
    private readonly FakeTimeProvider time;
    private readonly ActivityListener listener;
    private readonly ConcurrentQueue<Activity> passes;
    private bool disposed;

    private RecoveryHost(
        NpgsqlDataSource dataSource, ServiceProvider services, FakeTimeProvider time,
        ActivityListener listener, ConcurrentQueue<Activity> passes)
    {
        this.dataSource = dataSource;
        this.services = services;
        this.time = time;
        this.listener = listener;
        this.passes = passes;

        var meterFactory = services.GetRequiredService<IMeterFactory>();
        Captured = new MetricCollector<long>(meterFactory, PaymentsMetrics.MeterName, "payments.captured");
        Declined = new MetricCollector<long>(meterFactory, PaymentsMetrics.MeterName, "payments.declined");
        Logs = services.GetFakeLogCollector();
        recovery = services.GetRequiredService<PaymentRecovery>();
    }

    /// <summary>Everything recovery and PaymentCompletion logged.</summary>
    public FakeLogCollector Logs { get; }

    /// <summary>The <c>payments.captured</c> counter: the same metric the endpoint feeds.</summary>
    public MetricCollector<long> Captured { get; }

    /// <summary>The <c>payments.declined</c> counter.</summary>
    public MetricCollector<long> Declined { get; }

    /// <summary>
    /// Finished "payment recovery" spans, one per non-empty pass. The span ends after the whole pass,
    /// so once it is here, the pass's database writes, metrics and logs are done too.
    /// The listener is process-wide: with two hosts running, each sees the spans of both.
    /// </summary>
    public IReadOnlyCollection<Activity> Passes => passes;

    public static async Task<RecoveryHost> StartAsync(
        PostgresFixture db, FakeBank bank, FakeTimeProvider time, int batchSize = 100,
        CancellationToken cancellationToken = default)
    {
        // A pool of its own: closing it releases the advisory lock at once. A connection returned to a shared
        // pool keeps the lock until Npgsql reuses it and sends DISCARD ALL, and the next test would wait for it.
        var dataSource = NpgsqlDataSource.Create(db.ConnectionString);

        var services = new ServiceCollection()
            .AddMetrics()
            .AddFakeLogging()
            .AddSingleton(dataSource)
            .AddSingleton<TimeProvider>(time)
            .AddSingleton<IAcquirer>(bank)
            .AddSingleton<PaymentStore>()
            .AddSingleton<PaymentCompletion>()
            .AddSingleton<PaymentsMetrics>()
            .AddSingleton<PaymentRecovery>();

        services.AddOptions<FeeOptions>();
        services.Configure<RecoveryOptions>(options =>
        {
            options.Interval = Interval;      // fake time: a pause lasts until the test moves the clock
            options.StaleAfter = StaleAfter;
            options.BatchSize = batchSize;
            options.MaxParallelism = 1;       // the bank is asked in batch order, so tests can check the order
        });

        // Read before the listener exists. The first access to PaymentsTelemetry creates its ActivitySource,
        // and a source created inside ShouldListenTo breaks AddActivityListener, which calls it for every source.
        var sourceName = PaymentsTelemetry.ActivitySource.Name;

        var passes = new ConcurrentQueue<Activity>();
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.OperationName == "payment recovery")
                    passes.Enqueue(activity);
            },
        };
        ActivitySource.AddActivityListener(listener);

        // ValidateOnBuild: a missing registration fails right here, not somewhere inside the background loop.
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var host = new RecoveryHost(dataSource, provider, time, listener, passes);

        await host.recovery.StartAsync(cancellationToken);
        return host;
    }

    /// <summary>Waits until recovery has finished this many non-empty passes.</summary>
    public Task WaitForPassesAsync(int count) => Eventually.WaitUntilAsync(() => passes.Count >= count);

    /// <summary>
    /// Moves the fake clock by Interval until the condition holds. Recovery may not have started its pause
    /// yet when the clock moves the first time, so a single move is not always enough.
    /// </summary>
    public Task AdvanceUntilAsync(Func<Task<bool>> condition) =>
        Eventually.WaitUntilAsync(async () =>
        {
            time.Advance(Interval);
            return await condition();
        });

    /// <summary>Stops recovery the way a host shutdown does. Throws if the loop crashed instead of stopping.</summary>
    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        disposed = true;

        await recovery.StopAsync(CancellationToken.None);
        var crash = recovery.ExecuteTask is { IsFaulted: true } task ? task.Exception : null;

        listener.Dispose();
        Captured.Dispose();
        Declined.Dispose();
        await services.DisposeAsync();
        await dataSource.DisposeAsync();

        if (crash is not null)
            throw new InvalidOperationException("PaymentRecovery crashed instead of stopping", crash);
    }
}
