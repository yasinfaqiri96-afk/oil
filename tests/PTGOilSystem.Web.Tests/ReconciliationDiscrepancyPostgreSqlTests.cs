using PTGOilSystem.Web.Models.Reconciliation;
using PTGOilSystem.Web.Services.Reconciliation;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// InMemory هر عبارتی را در حافظه اجرا می‌کند، پس نمی‌تواند ثابت کند فیلتر و صفحه‌بندی
/// واقعاً به SQL ترجمه می‌شوند. این تست هر دسته را روی PostgreSQL واقعی (دیتابیس موقت
/// همان fixture) اجرا می‌کند تا هر عبارت ترجمه‌نشدنی همین‌جا بشکند، نه در محیط اجرا.
/// هیچ دیتابیس توسعه یا تولیدی لمس نمی‌شود.
/// </summary>
[Collection(AccountingPostgreSqlCollection.CollectionName)]
public sealed class ReconciliationDiscrepancyPostgreSqlTests(AccountingPostgreSqlFixture fixture)
{
    public static TheoryData<ReconciliationDiscrepancyCategory> Categories()
    {
        var data = new TheoryData<ReconciliationDiscrepancyCategory>();
        foreach (var category in ReconciliationDiscrepancyText.All)
        {
            data.Add(category);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Categories))]
    public async Task Category_Query_Translates_To_Sql_With_Count_And_Paging(
        ReconciliationDiscrepancyCategory category)
    {
        await using var db = fixture.CreateDbContext();
        var service = new ReconciliationService(db);

        var page = await service.BuildDiscrepancyPageAsync(category, page: 1, pageSize: 5);

        Assert.Equal(category, page.Category);
        Assert.True(page.TotalCount >= 0);
        Assert.True(page.Rows.Count <= 5);
    }

    [Fact]
    public async Task Date_Filtered_Counts_Translate_To_Sql_For_Every_Category()
    {
        await using var db = fixture.CreateDbContext();
        var service = new ReconciliationService(db);

        var counts = await service.BuildDiscrepancyCountsAsync(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31));

        Assert.Equal(ReconciliationDiscrepancyText.All.Count, counts.Count);
        Assert.All(counts, c => Assert.True(c.Count >= 0));
    }
}
