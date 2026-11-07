using System.Text.Json;

namespace Matrix;

/// <summary>
/// The stand as the services see it, from their appsettings.json: the matrix watches the same Postgres and Kafka
/// and holds the hot merchant against the same limit.
/// </summary>
public sealed record StandSettings(string PaymentsDatabase, string Kafka, string Redis, string LedgerGroup, int TokensPerSecond, int Burst)
{
    public static StandSettings Read(string repository)
    {
        var paymentsApi = ReadAppSettings(repository, "Payments.Api");
        var ledger = ReadAppSettings(repository, "Ledger.Service");
        var connections = paymentsApi.GetProperty("ConnectionStrings");
        var limit = paymentsApi.GetProperty("RateLimiting").GetProperty("Merchant");

        return new StandSettings(
            PaymentsDatabase: connections.GetProperty("Payments").GetString()!,
            Kafka: connections.GetProperty("Kafka").GetString()!,
            Redis: connections.GetProperty("Redis").GetString()!,
            LedgerGroup: ledger.GetProperty("Consumer").GetProperty("GroupId").GetString()!,
            TokensPerSecond: limit.GetProperty("TokensPerSecond").GetInt32(),
            Burst: limit.GetProperty("Burst").GetInt32());
    }

    /// <summary>The port of Kafka: its bootstrap server is host:port.</summary>
    public int KafkaPort => int.Parse(Kafka[(Kafka.LastIndexOf(':') + 1)..]);

    /// <summary>Host and port of Redis: its connection string starts with them, and the options follow after commas.</summary>
    public (string Host, int Port) RedisEndpoint
    {
        get
        {
            var endpoint = Redis.Split(',')[0];
            var colon = endpoint.LastIndexOf(':');
            return (endpoint[..colon], int.Parse(endpoint[(colon + 1)..]));
        }
    }

    private static JsonElement ReadAppSettings(string repository, string project)
    {
        // appsettings.json has comments, which plain JSON does not allow.
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(repository, "src", project, "appsettings.json")),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return document.RootElement.Clone();
    }
}
