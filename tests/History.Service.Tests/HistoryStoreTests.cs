using System.Text.Json;
using Contracts;
using History.Service.Consuming;
using History.Service.History;
using History.Service.Tests.Infrastructure;
using MongoDB.Bson;
using MongoDB.Driver;

namespace History.Service.Tests;

/// <summary>
/// The documents of the history in MongoDB (HistoryStore) and the events they are made of: a document per payment,
/// a repeat writes the same one, and the times keep the microseconds of PostgreSQL.
/// </summary>
[Collection("Mongo")]
public sealed class HistoryStoreTests(MongoFixture mongo)
{
    private static readonly DateTimeOffset Created = new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero).AddTicks(1_234_560);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Repeated_Event_Leaves_One_Document()
    {
        var store = mongo.NewStore(out _);
        var payment = Captured();

        await store.WriteAsync([payment], Ct);
        await store.WriteAsync([payment, payment], Ct);     // a batch read again after a failure, with a repeat in it

        var document = Assert.Single(await AllAsync(store));
        Assert.Equal(Stored(payment), PaymentDocument.FromBson(document));
    }

    [Fact]
    public async Task Batch_Of_Many_Payments_Is_Written_In_One_Go()
    {
        var store = mongo.NewStore(out _);
        var payments = Enumerable.Range(0, 1_000).Select(i => Captured(amount: 1_000 + i)).ToList();

        await store.WriteAsync(payments, Ct);

        Assert.Equal(1_000, await store.Payments.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct));
    }

    [Fact]
    public async Task Document_Keeps_What_The_Event_Says_To_The_Microsecond()
    {
        var store = mongo.NewStore(out _);
        var captured = new PaymentCaptured(Guid.CreateVersion7(), 42, 150_000, 3_000, "RUB", Created.AddSeconds(1), Created);
        var voided = new PaymentFinalized(Guid.CreateVersion7(), 42, 150_000, "RUB", PaymentFinalStatus.Voided, Created, Created.AddSeconds(2));

        await store.WriteAsync(
        [
            HistoryConsumer.Parse(Topics.PaymentsCaptured, JsonSerializer.Serialize(captured, ContractsJson.Default.PaymentCaptured)),
            HistoryConsumer.Parse(Topics.PaymentsFinalized, JsonSerializer.Serialize(voided, ContractsJson.Default.PaymentFinalized)),
        ], Ct);

        var documents = (await AllAsync(store)).Select(PaymentDocument.FromBson).ToDictionary(document => document.PaymentId);
        Assert.Equal(
            Stored(new PaymentDocument(captured.PaymentId, 42, 150_000, 3_000, "RUB", "captured", Created, Created.AddSeconds(1))),
            documents[captured.PaymentId]);
        Assert.Equal(
            Stored(new PaymentDocument(voided.PaymentId, 42, 150_000, null, "RUB", "voided", Created, Created.AddSeconds(2))),
            documents[voided.PaymentId]);
        Assert.Equal(Created, documents[voided.PaymentId].CreatedAt);       // the microseconds of created_at are kept
    }

    [Fact]
    public async Task Index_Of_The_List_Is_There_And_A_Second_Start_Changes_Nothing()
    {
        var store = mongo.NewStore(out _);

        await store.EnsureIndexesAsync(Ct);
        await store.EnsureIndexesAsync(Ct);

        var indexes = await (await store.Payments.Indexes.ListAsync(Ct)).ToListAsync(Ct);
        var index = Assert.Single(indexes, index => index["name"] == HistoryStore.MerchantCreatedIndex);
        Assert.Equal(new BsonDocument { { "merchantId", 1 }, { "createdAtUs", -1 }, { "_id", -1 } }, index["key"].AsBsonDocument);
    }

    [Theory]
    [InlineData("payments.captured", null)]
    [InlineData("payments.captured", "not json")]
    [InlineData("payments.captured", "null")]
    // A PaymentCaptured from before stage 5: no createdAt.
    [InlineData("payments.captured", """{"paymentId":"0193a0a0-0000-7000-8000-000000000001","merchantId":42,"amount":150000,"fee":3000,"currency":"RUB","capturedAt":"2026-11-24T12:00:00Z"}""")]
    public void Message_That_Is_Not_A_Payment_Of_The_History_Is_Malformed(string topic, string? json) =>
        Assert.Throws<JsonException>(() => HistoryConsumer.Parse(topic, json));

    private static PaymentDocument Captured(long amount = 150_000) =>
        new(Guid.CreateVersion7(), 42, amount, 3_000, "RUB", "captured", Created, Created.AddSeconds(1));

    // A BSON date holds milliseconds: finalizedAt, which only the lag and a person read, comes back without the rest.
    // createdAt keeps its microseconds in createdAtUs.
    private static PaymentDocument Stored(PaymentDocument document) => document with
    {
        FinalizedAt = new DateTimeOffset(
            document.FinalizedAt.UtcTicks - document.FinalizedAt.UtcTicks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero),
    };

    private static async Task<List<BsonDocument>> AllAsync(HistoryStore store) =>
        await store.Payments.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Ct);
}
