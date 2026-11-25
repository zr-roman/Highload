using Contracts;
using MongoDB.Bson;

namespace History.Service.History;

/// <summary>
/// A payment of the history: one document per payment with its final status, _id = the payment's id. Built from the
/// event of that status, PaymentCaptured or PaymentFinalized: a payment has exactly one of them, so a document is
/// written whole and never updated in parts, and a repeat of the event writes the same document.
/// </summary>
/// <remarks>
/// <para>
/// The time of creation is kept twice. createdAtUs, microseconds since 1970, is what the list orders and pages by: a
/// BSON date holds milliseconds, and payments created in the same millisecond would then come in another order than
/// the list from PostgreSQL, where created_at holds microseconds. createdAt, a date, is for a person reading the
/// documents in mongosh.
/// </para>
/// <para>
/// _id is a UUID of subtype 4 in the byte order of RFC 4122 (GuidRepresentation.Standard), the order PostgreSQL
/// compares uuid in: the two lists break ties of time the same way.
/// </para>
/// </remarks>
public sealed record PaymentDocument(
    Guid PaymentId,
    long MerchantId,
    long Amount,
    long? Fee,
    string Currency,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset FinalizedAt)
{
    public const string Captured = "captured";

    public static PaymentDocument From(PaymentCaptured captured) => new(
        captured.PaymentId, captured.MerchantId, captured.Amount, captured.Fee, captured.Currency, Captured,
        captured.CreatedAt, captured.CapturedAt);

    public static PaymentDocument From(PaymentFinalized finalized) => new(
        finalized.PaymentId, finalized.MerchantId, finalized.Amount, Fee: null, finalized.Currency, finalized.Status,
        finalized.CreatedAt, finalized.FinalizedAt);

    public static long Microseconds(DateTimeOffset time) =>
        (time.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / TimeSpan.TicksPerMicrosecond;

    public BsonDocument ToBson() => new()
    {
        { "_id", new BsonBinaryData(PaymentId, GuidRepresentation.Standard) },
        { "merchantId", MerchantId },
        { "amount", Amount },
        { "fee", Fee is { } fee ? fee : BsonNull.Value },
        { "currency", Currency },
        { "status", Status },
        { "createdAtUs", Microseconds(CreatedAt) },
        { "createdAt", new BsonDateTime(CreatedAt.UtcDateTime) },
        { "finalizedAt", new BsonDateTime(FinalizedAt.UtcDateTime) },
    };

    public static PaymentDocument FromBson(BsonDocument document) => new(
        document["_id"].AsBsonBinaryData.ToGuid(GuidRepresentation.Standard),
        document["merchantId"].AsInt64,
        document["amount"].AsInt64,
        document["fee"].IsBsonNull ? null : document["fee"].AsInt64,
        document["currency"].AsString,
        document["status"].AsString,
        DateTimeOffset.UnixEpoch.AddTicks(document["createdAtUs"].AsInt64 * TimeSpan.TicksPerMicrosecond),
        new DateTimeOffset(document["finalizedAt"].ToUniversalTime(), TimeSpan.Zero));
}
