using System.Text;
using Npgsql;

namespace Payments.Api.Payments;

/// <summary>
/// A page of a merchant's payments, newest first: GET /merchants/{id}/payments. Read straight from payments, by the
/// index payments_merchant_created (SchemaAfterStart): the base the history of stage 5, read from MongoDB, is measured
/// against, and the list the API has until then.
/// </summary>
/// <remarks>
/// Pages by keyset, not by OFFSET: a page starts after the (created_at, id) of the last payment of the page before,
/// so the index is read from that point and the price of a page does not grow with its number. created_at alone
/// would not do: payments created in the same microsecond are told apart by id, so no payment falls between two
/// pages or comes twice. Payments created after the first page do not shift the next ones: they come before it.
/// The status filter is applied on top of the index, which has no status: a page of a status a merchant rarely has
/// reads more of the index to fill, up to all of the merchant's payments within from and to.
/// </remarks>
public sealed class PaymentList(NpgsqlDataSource dataSource)
{
    public const int MaxLimit = 100;
    public const int DefaultLimit = 50;

    public async Task<PaymentPage> PageAsync(PaymentListQuery query, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(query);

        var items = new List<PaymentResponse>(query.Limit + 1);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(new PaymentResponse(
                    reader.GetGuid(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetInt64(4), reader.GetString(5), reader.GetFieldValue<DateTimeOffset>(6)));
            }
        }

        if (items.Count <= query.Limit)
            return new PaymentPage(items, NextCursor: null);

        items.RemoveAt(items.Count - 1);
        var last = items[^1];
        return new PaymentPage(items, PaymentListCursor.Encode(new PaymentListCursor(last.CreatedAt, last.Id)));
    }

    /// <summary>The statement of a page; also what PaymentListTests give EXPLAIN, to see that it reads the index.</summary>
    public NpgsqlCommand CreateCommand(PaymentListQuery query)
    {
        // Only the conditions the query has: a "$n IS NULL OR ..." for each would leave the planner a generic plan
        // that cannot use the index for all of them.
        var sql = new StringBuilder("""
            SELECT id, merchant_id, amount, currency, fee, status, created_at
            FROM payments
            WHERE merchant_id = $1
            """);
        var parameters = new List<NpgsqlParameter> { new NpgsqlParameter<long> { TypedValue = query.MerchantId } };

        // A condition with its parameters, {0}, {1}... in it becoming their $n.
        void Add(string condition, params NpgsqlParameter[] values)
        {
            var placeholders = values.Select((_, i) => (object)$"${parameters.Count + i + 1}").ToArray();
            parameters.AddRange(values);
            sql.Append("\n  AND ").AppendFormat(condition, placeholders);
        }

        if (query.From is { } from)
            Add("created_at >= {0}", new NpgsqlParameter<DateTimeOffset> { TypedValue = from });
        if (query.To is { } to)
            Add("created_at < {0}", new NpgsqlParameter<DateTimeOffset> { TypedValue = to });
        if (query.Status is { } status)
            Add("status = {0}", new NpgsqlParameter<string> { TypedValue = status.ToDb() });
        if (query.After is { } after)
        {
            // A row comparison: Postgres reads it as one bound on the index, in its order of (created_at, id).
            Add("(created_at, id) < ({0}, {1})",
                new NpgsqlParameter<DateTimeOffset> { TypedValue = after.CreatedAt },
                new NpgsqlParameter<Guid> { TypedValue = after.Id });
        }

        // One more than the page: whether there is a next page, without a count.
        parameters.Add(new NpgsqlParameter<int> { TypedValue = query.Limit + 1 });
        sql.Append($"\nORDER BY created_at DESC, id DESC\nLIMIT ${parameters.Count}");

        var command = dataSource.CreateCommand(sql.ToString());
        command.Parameters.AddRange(parameters.ToArray());
        return command;
    }
}

/// <summary>What GET /merchants/{id}/payments asks for, checked: <see cref="PaymentListQuery.TryParse"/>.</summary>
/// <param name="From">Created at or after; null: from the first payment.</param>
/// <param name="To">Created before; null: up to now.</param>
/// <param name="After">The page starts after this payment; null: the first page.</param>
public sealed record PaymentListQuery(
    long MerchantId, DateTimeOffset? From, DateTimeOffset? To, PaymentStatus? Status, PaymentListCursor? After, int Limit)
{
    /// <summary>The query of the request, or the errors of its parameters, by name, for a 400.</summary>
    public static PaymentListQuery? TryParse(
        long merchantId, DateTimeOffset? from, DateTimeOffset? to, string? status, string? cursor, int? limit,
        out Dictionary<string, string[]> errors)
    {
        errors = [];

        if (merchantId <= 0)
            errors["merchantId"] = ["The merchant id must be positive."];
        if (from is { } f && to is { } t && f >= t)
            errors["to"] = ["'to' must be later than 'from'."];

        PaymentStatus? parsedStatus = null;
        if (status is not null)
        {
            try
            {
                parsedStatus = PaymentStatusDb.Parse(status);
            }
            catch (InvalidOperationException)
            {
                errors["status"] = ["Unknown status: one of created, authorized, captured, declined, voided."];
            }
        }

        PaymentListCursor? after = null;
        if (cursor is not null && (after = PaymentListCursor.TryDecode(cursor)) is null)
            errors["cursor"] = ["The cursor is not one this API gave: take nextCursor of the previous page as it is."];

        if (limit is < 1 or > PaymentList.MaxLimit)
            errors["limit"] = [$"The limit must be from 1 to {PaymentList.MaxLimit}."];

        return errors.Count > 0
            ? null
            : new PaymentListQuery(merchantId, from, to, parsedStatus, after, limit ?? PaymentList.DefaultLimit);
    }
}

/// <summary>
/// Where the next page starts: the created_at and the id of the last payment of the page before. Opaque to the
/// client, base64url of 24 bytes: the microseconds since 1970 of created_at, the precision of timestamptz, so the
/// cursor names that payment exactly, and the id.
/// </summary>
/// <remarks>
/// The cursor carries no filter: a client that changes from, to or status between pages gets the pages of the new
/// query after that payment, which is what it asked for, and nothing breaks.
/// </remarks>
public sealed record PaymentListCursor(DateTimeOffset CreatedAt, Guid Id)
{
    private const int Length = sizeof(long) + 16;

    public static string Encode(PaymentListCursor cursor)
    {
        Span<byte> bytes = stackalloc byte[Length];
        var microseconds = (cursor.CreatedAt.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / TimeSpan.TicksPerMicrosecond;
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(bytes, microseconds);
        cursor.Id.TryWriteBytes(bytes[sizeof(long)..], bigEndian: true, out _);
        return System.Buffers.Text.Base64Url.EncodeToString(bytes);
    }

    /// <summary>The cursor, or null for a string this API did not give: a 400, not a page of something else.</summary>
    public static PaymentListCursor? TryDecode(string value)
    {
        Span<byte> bytes = stackalloc byte[Length + 3];  // room for a too long value to show as one
        var status = System.Buffers.Text.Base64Url.DecodeFromChars(value, bytes, out var read, out var written);
        if (status != System.Buffers.OperationStatus.Done || read != value.Length || written != Length)
            return null;

        var microseconds = System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(bytes);
        var ticks = DateTimeOffset.UnixEpoch.UtcTicks + microseconds * TimeSpan.TicksPerMicrosecond;
        if (microseconds < 0 || ticks > DateTimeOffset.MaxValue.UtcTicks)
            return null;

        return new PaymentListCursor(new DateTimeOffset(ticks, TimeSpan.Zero), new Guid(bytes[sizeof(long)..Length], bigEndian: true));
    }
}

/// <summary>A page of GET /merchants/{id}/payments; NextCursor is null on the last page.</summary>
public sealed record PaymentPage(IReadOnlyList<PaymentResponse> Items, string? NextCursor);
