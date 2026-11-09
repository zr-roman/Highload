using StackExchange.Redis;
using StackExchange.Redis.KeyspaceIsolation;
using Testcontainers.Redis;

namespace Payments.Api.Tests.Infrastructure;

/// <summary>A Redis of its own for the tests of the rate limiter, the same version as in docker-compose.yml.</summary>
public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer container = new RedisBuilder("redis:7.4-alpine").Build();

    private IConnectionMultiplexer connection = null!;

    public async ValueTask InitializeAsync()
    {
        await container.StartAsync();
        connection = await ConnectionMultiplexer.ConnectAsync(container.GetConnectionString());
    }

    public async ValueTask DisposeAsync()
    {
        await connection.DisposeAsync();
        await container.DisposeAsync();
    }

    /// <summary>Keys of their own for each test, as the service has its prefix: buckets of one test never meet another's.</summary>
    public IDatabase NewDatabase() => connection.GetDatabase().WithKeyPrefix($"test:{Guid.NewGuid():N}:");
}

[CollectionDefinition("Redis")]
public sealed class RedisCollection : ICollectionFixture<RedisFixture>;
