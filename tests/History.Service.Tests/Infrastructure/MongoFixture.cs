using History.Service.History;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Testcontainers.MongoDb;

namespace History.Service.Tests.Infrastructure;

/// <summary>MongoDB as on the stand: the image of docker-compose.yml, one node.</summary>
public sealed class MongoFixture : IAsyncLifetime
{
    private readonly MongoDbContainer container = new MongoDbBuilder("mongo:8.0").Build();

    public async ValueTask InitializeAsync() => await container.StartAsync();

    public async ValueTask DisposeAsync() => await container.DisposeAsync();

    /// <summary>MongoDB stops answering, as a server that hangs: connections stay open, nothing comes back.</summary>
    public Task PauseAsync() => container.PauseAsync();

    public Task UnpauseAsync() => container.UnpauseAsync();

    /// <summary>A store on a database of its own: the tests of a class do not see each other's documents.</summary>
    public HistoryStore NewStore(out IMongoClient client)
    {
        var settings = MongoClientSettings.FromConnectionString(container.GetConnectionString());
        // A paused MongoDB must fail a write within seconds.
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(2);
        settings.SocketTimeout = TimeSpan.FromSeconds(2);
        client = new MongoClient(settings);
        return new HistoryStore(client, Options.Create(new HistoryOptions { Database = $"history_{Guid.NewGuid():N}" }));
    }
}

[CollectionDefinition("Mongo")]
public sealed class MongoCollection : ICollectionFixture<MongoFixture>;
