using PTGOilSystem.Web.Models.Entities;
using PTGOilSystem.Web.Services.Backups;
using Xunit;

namespace PTGOilSystem.Web.Tests;

/// <summary>
/// سیاست نگهداری ۷ روزانه / ۴ هفتگی / ۶ ماهانه.
/// اولین بکاپ هر ماه «ماهانه»، اولین بکاپ هر هفته «هفتگی» و بقیه «روزانه» می‌شوند.
/// </summary>
public class BackupRetentionPlannerTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    [Fact]
    public void The_Very_First_Backup_Is_Monthly()
    {
        var tier = BackupRetentionPlanner.ClassifyTier(new DateTime(2026, 3, 10, 2, 0, 0, DateTimeKind.Utc), [], Utc);

        Assert.Equal(BackupRetentionTier.Monthly, tier);
    }

    [Fact]
    public void The_First_Backup_Of_A_New_Month_Is_Monthly()
    {
        DateTime[] existing = [new(2026, 3, 30, 2, 0, 0, DateTimeKind.Utc)];

        var tier = BackupRetentionPlanner.ClassifyTier(new DateTime(2026, 4, 1, 2, 0, 0, DateTimeKind.Utc), existing, Utc);

        Assert.Equal(BackupRetentionTier.Monthly, tier);
    }

    [Fact]
    public void The_First_Backup_Of_A_New_Week_In_The_Same_Month_Is_Weekly()
    {
        // ۱۰ مارس سه‌شنبه؛ ۱۴ مارس شنبه و آغاز هفتهٔ بعد است.
        DateTime[] existing = [new(2026, 3, 10, 2, 0, 0, DateTimeKind.Utc)];

        var tier = BackupRetentionPlanner.ClassifyTier(new DateTime(2026, 3, 14, 2, 0, 0, DateTimeKind.Utc), existing, Utc);

        Assert.Equal(BackupRetentionTier.Weekly, tier);
    }

    [Fact]
    public void A_Second_Backup_In_The_Same_Week_Is_Daily()
    {
        DateTime[] existing =
        [
            new(2026, 3, 10, 2, 0, 0, DateTimeKind.Utc),
            new(2026, 3, 11, 2, 0, 0, DateTimeKind.Utc)
        ];

        var tier = BackupRetentionPlanner.ClassifyTier(new DateTime(2026, 3, 12, 2, 0, 0, DateTimeKind.Utc), existing, Utc);

        Assert.Equal(BackupRetentionTier.Daily, tier);
    }

    [Fact]
    public void Each_Tier_Keeps_Only_Its_Configured_Newest_Copies()
    {
        var policy = new BackupRetentionPolicy(KeepDaily: 7, KeepWeekly: 4, KeepMonthly: 6);

        var candidates = new List<BackupRetentionCandidate>();
        for (var i = 0; i < 10; i++)
        {
            candidates.Add(new BackupRetentionCandidate(100 + i, new DateTime(2026, 3, 1, 2, 0, 0, DateTimeKind.Utc).AddDays(i), BackupRetentionTier.Daily));
        }
        for (var i = 0; i < 6; i++)
        {
            candidates.Add(new BackupRetentionCandidate(200 + i, new DateTime(2026, 1, 3, 2, 0, 0, DateTimeKind.Utc).AddDays(7 * i), BackupRetentionTier.Weekly));
        }
        for (var i = 0; i < 8; i++)
        {
            candidates.Add(new BackupRetentionCandidate(300 + i, new DateTime(2025, 8, 1, 2, 0, 0, DateTimeKind.Utc).AddMonths(i), BackupRetentionTier.Monthly));
        }

        var expired = BackupRetentionPlanner.SelectExpired(candidates, policy);

        Assert.Equal(3, expired.Count(candidate => candidate.Id is >= 100 and < 200)); // 10 - 7
        Assert.Equal(2, expired.Count(candidate => candidate.Id is >= 200 and < 300)); // 6 - 4
        Assert.Equal(2, expired.Count(candidate => candidate.Id >= 300));              // 8 - 6
    }

    [Fact]
    public void The_Newest_Copy_Of_A_Tier_Is_Never_Expired()
    {
        var policy = new BackupRetentionPolicy(1, 1, 1);
        var newest = new BackupRetentionCandidate(2, new DateTime(2026, 3, 11, 2, 0, 0, DateTimeKind.Utc), BackupRetentionTier.Daily);
        var older = new BackupRetentionCandidate(1, new DateTime(2026, 3, 10, 2, 0, 0, DateTimeKind.Utc), BackupRetentionTier.Daily);

        var expired = BackupRetentionPlanner.SelectExpired([older, newest], policy);

        Assert.Equal(older.Id, Assert.Single(expired).Id);
    }

    [Fact]
    public void Nothing_Expires_While_The_Tier_Is_Under_Its_Limit()
    {
        var policy = BackupRetentionPolicy.FromSettings(new BackupSetting());
        var candidates = Enumerable.Range(0, 7)
            .Select(i => new BackupRetentionCandidate(i + 1, new DateTime(2026, 3, 1, 2, 0, 0, DateTimeKind.Utc).AddDays(i), BackupRetentionTier.Daily));

        Assert.Empty(BackupRetentionPlanner.SelectExpired(candidates, policy));
    }

    [Fact]
    public void Default_Settings_Map_To_The_Required_Seven_Four_Six_Policy()
    {
        var policy = BackupRetentionPolicy.FromSettings(new BackupSetting());

        Assert.Equal(7, policy.KeepDaily);
        Assert.Equal(4, policy.KeepWeekly);
        Assert.Equal(6, policy.KeepMonthly);
    }
}
