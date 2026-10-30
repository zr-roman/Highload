namespace Payments.Api.Tests.Infrastructure;

[Collection("Postgres")]
public sealed class PostgresFixtureTests(PostgresFixture db)
{
    [Fact]
    public async Task Inserted_Payment_Can_Be_Read_Back()
    {
        await db.ResetAsync(TestContext.Current.CancellationToken);

        var id = await db.InsertPaymentAsync(TimeSpan.FromMinutes(1), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("created", await db.GetStatusAsync(id, TestContext.Current.CancellationToken));
    }
}