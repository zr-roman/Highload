using Npgsql;
using Payments.Api.Database;
using Payments.Api.Payments;
using Payments.Api.Tests.Infrastructure;
using Platform;

namespace Payments.Api.Tests.Payments;

/// <summary>
/// The list of a merchant's payments from PostgreSQL (PaymentList), on a real Postgres with the index of
/// SchemaAfterStart: the pages follow one another with no payment missing or repeated, also when payments share a
/// created_at and while new ones come in, and the statement reads the index without a sort.
/// </summary>
[Collection("Postgres")]
public sealed class PaymentListTests(PostgresFixture db) : IAsyncLifetime
{
    private const long Merchant = 7;
    private const long OtherMerchant = 8;

    private static readonly DateTimeOffset Start = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    private readonly PaymentList list = new(db.DataSource);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await db.ResetAsync(Ct);
        foreach (var (name, definition) in SchemaAfterStart.Indexes)
            await DatabaseSchema.CreateIndexConcurrentlyAsync(db.DataSource, name, definition, Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Pages_Have_No_Gaps_Or_Repeats_When_Payments_Share_A_Created_At()
    {
        // 30 payments of the merchant, three in each microsecond, and those of another merchant between them.
        for (var i = 0; i < 30; i++)
        {
            await InsertAsync(Merchant, Start.AddMilliseconds(i / 3));
            await InsertAsync(OtherMerchant, Start.AddMilliseconds(i / 3));
        }

        var pages = await AllPagesAsync(Query(limit: 4));

        Assert.All(pages, page => Assert.True(page.Items.Count <= 4));
        Assert.Equal(8, pages.Count);                                   // 7 pages of 4 and the last of 2
        Assert.Null(pages[^1].NextCursor);
        Assert.Equal(await IdsInOrderAsync(Merchant), pages.SelectMany(page => page.Items).Select(item => item.Id).ToList());
    }

    [Fact]
    public async Task Last_Full_Page_Has_No_Cursor()
    {
        for (var i = 0; i < 8; i++)
            await InsertAsync(Merchant, Start.AddSeconds(i));

        var pages = await AllPagesAsync(Query(limit: 4));

        // Without the one row more than the page, the second page would end with a cursor to an empty third one.
        Assert.Equal(2, pages.Count);
        Assert.NotNull(pages[0].NextCursor);
        Assert.Null(pages[1].NextCursor);
    }

    [Fact]
    public async Task Payments_Created_While_Paging_Do_Not_Shift_The_Next_Pages()
    {
        for (var i = 0; i < 10; i++)
            await InsertAsync(Merchant, Start.AddSeconds(i));
        var before = await IdsInOrderAsync(Merchant);

        var first = await list.PageAsync(Query(limit: 4), Ct);
        for (var i = 0; i < 3; i++)
            await InsertAsync(Merchant, Start.AddMinutes(1).AddSeconds(i));    // newer than all: before the first page
        var rest = await AllPagesAsync(Query(limit: 4) with { After = PaymentListCursor.TryDecode(first.NextCursor!) });

        Assert.Equal(before, first.Items.Concat(rest.SelectMany(page => page.Items)).Select(item => item.Id).ToList());
    }

    [Fact]
    public async Task Status_And_Time_Window_Narrow_The_List()
    {
        var old = await InsertAsync(Merchant, Start, "captured");
        var inside = await InsertAsync(Merchant, Start.AddHours(1), "captured");
        await InsertAsync(Merchant, Start.AddHours(1).AddSeconds(1), "declined");
        var atTo = await InsertAsync(Merchant, Start.AddHours(2), "captured");

        var page = await list.PageAsync(
            Query(limit: 10) with { From = Start.AddMinutes(30), To = Start.AddHours(2), Status = PaymentStatus.Captured }, Ct);

        // from is inclusive and to exclusive, so consecutive windows cover every payment once.
        Assert.Equal([inside], page.Items.Select(item => item.Id));
        Assert.DoesNotContain(old, page.Items.Select(item => item.Id));
        Assert.DoesNotContain(atTo, page.Items.Select(item => item.Id));
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task Page_Is_What_The_Rows_Say()
    {
        var id = await InsertAsync(Merchant, Start, "captured", fee: 3_000);

        var item = Assert.Single((await list.PageAsync(Query(limit: 10), Ct)).Items);

        Assert.Equal(new PaymentResponse(id, Merchant, 150_000, "RUB", 3_000, "captured", Start), item);
    }

    [Fact]
    public async Task Pages_Read_The_Index_From_The_Cursor_On()
    {
        // Enough rows for the planner to prefer the index: 50 merchants, 400 payments each.
        await using (var fill = db.DataSource.CreateCommand("""
            INSERT INTO payments (id, merchant_id, idempotency_key, request_hash, amount, currency, status, created_at)
            SELECT gen_random_uuid(), 1 + n % 50, n::text, 'test', 150000, 'RUB',
                   (ARRAY['captured', 'declined', 'voided'])[1 + n % 3], $1::timestamptz + n * interval '1 ms'
            FROM generate_series(1, 20000) AS n
            """))
        {
            fill.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = Start });
            await fill.ExecuteNonQueryAsync(Ct);
        }
        await using (var analyze = db.DataSource.CreateCommand("ANALYZE payments"))
            await analyze.ExecuteNonQueryAsync(Ct);

        var cursor = new PaymentListCursor(Start.AddSeconds(10), Guid.NewGuid());
        foreach (var query in new[]
        {
            Query(limit: 50),
            Query(limit: 50) with { After = cursor },
            Query(limit: 50) with { After = cursor, Status = PaymentStatus.Captured, From = Start, To = Start.AddSeconds(15) },
        })
        {
            await using var command = list.CreateCommand(query);
            command.CommandText = "EXPLAIN " + command.CommandText;

            var plan = new List<string>();
            await using (var reader = await command.ExecuteReaderAsync(Ct))
                while (await reader.ReadAsync(Ct))
                    plan.Add(reader.GetString(0));

            var text = string.Join("\n", plan);
            Assert.Contains("payments_merchant_created", text);
            if (query.After is not null)
                Assert.Matches(@"Index Cond: .*ROW\(created_at, id\) <", text);   // the cursor bounds the scan, not a filter

            // In the order of the index, so no sort and the scan stops at the limit. A narrow window of time with few
            // rows may take a bitmap scan and a sort of those few instead, which is the planner's call and cheap.
            if (query.From is null && query.To is null)
                Assert.DoesNotContain("Sort", text);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a cursor")]
    [InlineData("AAAA")]                                                // too short
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]                // too long
    [InlineData("_____________________________w")]                     // negative time
    public void Cursor_The_Api_Did_Not_Give_Is_An_Error(string cursor)
    {
        Assert.Null(PaymentListCursor.TryDecode(cursor));

        Assert.Null(PaymentListQuery.TryParse(Merchant, null, null, null, cursor, null, out var errors));
        Assert.Contains("cursor", errors.Keys);
    }

    [Fact]
    public void Cursor_Names_The_Payment_To_The_Microsecond()
    {
        var cursor = new PaymentListCursor(Start.AddTicks(1_234_560), Guid.CreateVersion7());

        Assert.Equal(cursor, PaymentListCursor.TryDecode(PaymentListCursor.Encode(cursor)));
        Assert.Equal(32, PaymentListCursor.Encode(cursor).Length);
    }

    [Theory]
    [InlineData(0, null, null, null, "merchantId")]
    [InlineData(Merchant, 0, null, null, "limit")]
    [InlineData(Merchant, 101, null, null, "limit")]
    [InlineData(Merchant, null, "paid", null, "status")]
    [InlineData(Merchant, null, null, true, "to")]
    public void Wrong_Parameters_Are_Errors(long merchantId, int? limit, string? status, bool? toBeforeFrom, string error)
    {
        var query = PaymentListQuery.TryParse(
            merchantId, Start, toBeforeFrom == true ? Start : null, status, cursor: null, limit, out var errors);

        Assert.Null(query);
        Assert.Equal([error], errors.Keys);
    }

    [Fact]
    public void Defaults_Are_The_First_Page_Of_50()
    {
        var query = PaymentListQuery.TryParse(Merchant, null, null, null, null, null, out var errors);

        Assert.Empty(errors);
        Assert.Equal(new PaymentListQuery(Merchant, null, null, null, null, PaymentList.DefaultLimit), query);
    }

    private static PaymentListQuery Query(int limit) => new(Merchant, null, null, null, null, limit);

    private async Task<List<PaymentPage>> AllPagesAsync(PaymentListQuery query)
    {
        var pages = new List<PaymentPage>();
        while (true)
        {
            var page = await list.PageAsync(query, Ct);
            pages.Add(page);
            if (page.NextCursor is null)
                return pages;

            Assert.True(pages.Count < 100, "the pages do not end");
            query = query with { After = PaymentListCursor.TryDecode(page.NextCursor) };
        }
    }

    // The order the pages must give, by Postgres itself: uuid compares as bytes in Postgres and otherwise in .NET.
    private async Task<List<Guid>> IdsInOrderAsync(long merchantId)
    {
        await using var command = db.DataSource.CreateCommand(
            "SELECT id FROM payments WHERE merchant_id = $1 ORDER BY created_at DESC, id DESC");
        command.Parameters.Add(new NpgsqlParameter<long> { TypedValue = merchantId });

        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
            ids.Add(reader.GetGuid(0));
        return ids;
    }

    private async Task<Guid> InsertAsync(long merchantId, DateTimeOffset createdAt, string status = "captured", long? fee = null)
    {
        var id = Guid.CreateVersion7();
        await using var command = db.DataSource.CreateCommand("""
            INSERT INTO payments (id, merchant_id, idempotency_key, request_hash, amount, currency, fee, status, created_at)
            VALUES ($1, $2, $1::text, 'test', 150000, 'RUB', $3, $4, $5)
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        command.Parameters.Add(new NpgsqlParameter<long> { TypedValue = merchantId });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)fee ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = status });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = createdAt });
        await command.ExecuteNonQueryAsync(Ct);
        return id;
    }
}
