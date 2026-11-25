using MongoDB.Bson;
using MongoDB.Driver;

namespace History.Service.History;

public sealed class HistoryOptions
{
    public string Database { get; set; } = "history";
}

/// <summary>The collection payments of MongoDB: the history of payments that History.Service writes and reads.</summary>
public sealed class HistoryStore(IMongoClient client, Microsoft.Extensions.Options.IOptions<HistoryOptions> options)
{
    public const string Collection = "payments";

    /// <summary>
    /// The list of a merchant's payments, newest first, page by page (task 5 of stage 5): the same order as the index
    /// payments_merchant_created of PostgreSQL, so a page reads the index from the cursor on and stops at the limit.
    /// </summary>
    public const string MerchantCreatedIndex = "merchant_created";

    public IMongoCollection<BsonDocument> Payments { get; } =
        client.GetDatabase(options.Value.Database).GetCollection<BsonDocument>(Collection);

    /// <summary>On every start: a no-op when the index is there with the same keys.</summary>
    public async Task EnsureIndexesAsync(CancellationToken cancellationToken)
    {
        var keys = Builders<BsonDocument>.IndexKeys.Ascending("merchantId").Descending("createdAtUs").Descending("_id");
        await Payments.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(keys, new CreateIndexOptions { Name = MerchantCreatedIndex }),
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Writes the documents of a batch in one request: a replace with upsert for each, by _id. A document that is
    /// there already is replaced by the same one, so a repeated event, or a batch written again after a failure,
    /// changes nothing. Unordered: one failed document does not stop the rest, and the server may write them in
    /// parallel. A payment twice in one batch is written once, its last copy: they are the same anyway.
    /// </summary>
    public async Task WriteAsync(IReadOnlyCollection<PaymentDocument> documents, CancellationToken cancellationToken)
    {
        var models = documents
            .GroupBy(document => document.PaymentId)
            .Select(group => group.Last())
            .Select(document => new ReplaceOneModel<BsonDocument>(
                Builders<BsonDocument>.Filter.Eq("_id", new BsonBinaryData(document.PaymentId, GuidRepresentation.Standard)),
                document.ToBson()) { IsUpsert = true })
            .ToList();

        if (models.Count == 0)
            return;

        await Payments.BulkWriteAsync(models, new BulkWriteOptions { IsOrdered = false }, cancellationToken);
    }
}
