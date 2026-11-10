using Microsoft.EntityFrameworkCore;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Domain.Enums;
using Xunit;

namespace VolleyballClub.Tests;

/// <summary>관리자 출석 통계 — 기간 경계 포함, 미매핑 행 제외, 비활성 부원 포함, 활동 0회 null 계약.</summary>
public class AdminAttendanceStatsTests
{
    [Fact]
    public async Task Stats_CountsOnlyActivitiesInsideRange()
    {
        await using var host = new ServiceHost();
        host.AsAdmin();
        var alice = await host.SeedUserAsync("김민수", "202512001");
        var bob = await host.SeedUserAsync("박지훈", "202512002");

        // 기간 양 끝(10-01, 10-08)만 분모에 들어가고 하루 밖(09-30, 10-09)은 빠진다
        await host.SeedActivityAsync(new DateOnly(2026, 9, 30), ActivityStatus.ActivityClosed);
        await host.SeedActivityAsync(new DateOnly(2026, 10, 1), ActivityStatus.ActivityClosed);
        await host.SeedActivityAsync(new DateOnly(2026, 10, 8), ActivityStatus.ActivityClosed);
        await host.SeedActivityAsync(new DateOnly(2026, 10, 9), ActivityStatus.ActivityClosed);
        await AddAttendanceDirectAsync(host, new DateOnly(2026, 10, 1), alice);
        await AddAttendanceDirectAsync(host, new DateOnly(2026, 10, 8), alice);
        await AddAttendanceDirectAsync(host, new DateOnly(2026, 10, 9), alice); // 기간 밖 — 집계 제외
        await AddAttendanceDirectAsync(host, new DateOnly(2026, 10, 9), bob);   // 기간 밖 — 집계 제외

        var stats = await host.Attendance.GetAdminStatsAsync(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 8));

        Assert.Equal(new DateOnly(2026, 10, 1), stats.Start);
        Assert.Equal(new DateOnly(2026, 10, 8), stats.End);
        Assert.Equal(2, stats.ActivityCount);
        Assert.Equal(0, stats.UnmappedRowCount);
        Assert.Equal(2, stats.TargetMemberCount);
        Assert.Equal(2, stats.AttendanceCount);

        var aliceRow = stats.Rows.Single(r => r.UserId == alice);
        Assert.Equal(2, aliceRow.AttendanceCount);
        Assert.Equal(100.0, aliceRow.RatePercent ?? double.NaN); // 2 / 2회
        var bobRow = stats.Rows.Single(r => r.UserId == bob);
        Assert.Equal(0, bobRow.AttendanceCount);
        Assert.Equal(0.0, bobRow.RatePercent ?? double.NaN);
        Assert.Equal(50.0, stats.AverageRatePercent ?? double.NaN); // (2+0) / (2명 × 2회)
    }

    [Fact]
    public async Task Stats_ExcludesUnmappedRows_AndReportsThem()
    {
        await using var host = new ServiceHost();
        host.AsAdmin();
        var alice = await host.SeedUserAsync("김민수", "202512001");
        var activity = await host.SeedActivityAsync(new DateOnly(2026, 10, 1), ActivityStatus.ActivityClosed);
        await AddAttendanceDirectAsync(host, new DateOnly(2026, 10, 1), alice);

        // 학기 행은 있으나 계정과 매핑되지 않은 출석 — 부원 집계에서 빠지고 건수만 보고된다
        var unmapped = await host.SheetRows.AddRowAsync(new SemesterMemberCreateRequest(0, "외부인", null));
        await host.SheetRows.SetRowAttendanceAsync(unmapped.Id, activity.Id, true);

        var stats = await host.Attendance.GetAdminStatsAsync(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 1));

        Assert.Equal(1, stats.ActivityCount);
        Assert.Equal(1, stats.UnmappedRowCount);
        Assert.Equal(1, stats.AttendanceCount); // 미매핑 1건은 총 출석에도 들어가지 않는다
        var row = Assert.Single(stats.Rows);
        Assert.Equal(alice, row.UserId);
        Assert.Equal(1, row.AttendanceCount);
    }

    [Fact]
    public async Task Stats_IncludesAttendedWithdrawnMember()
    {
        await using var host = new ServiceHost();
        host.AsAdmin();
        var alice = await host.SeedUserAsync("김민수", "202512001");
        var withdrawn = await host.SeedUserAsync("박지훈", "202512002", MemberStatus.Withdrawn);
        var carol = await host.SeedUserAsync("이정우", "202512003"); // 활성 · 기간 출석 0회
        var expelledWithout = await host.SeedUserAsync("최민", "202512004", MemberStatus.Expelled);

        await host.SeedActivityAsync(new DateOnly(2026, 10, 1), ActivityStatus.ActivityClosed);
        await AddAttendanceDirectAsync(host, new DateOnly(2026, 10, 1), alice);
        await AddAttendanceDirectAsync(host, new DateOnly(2026, 10, 1), withdrawn);

        var stats = await host.Attendance.GetAdminStatsAsync(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 1));

        Assert.Equal(1, stats.ActivityCount);
        Assert.Equal(3, stats.TargetMemberCount); // 활성 2명 + 기간 출석이 있는 비활성 1명

        var withdrawnRow = stats.Rows.Single(r => r.UserId == withdrawn);
        Assert.Equal(MemberStatus.Withdrawn, withdrawnRow.Status);
        Assert.Equal(1, withdrawnRow.AttendanceCount);

        var carolRow = stats.Rows.Single(r => r.UserId == carol);
        Assert.Equal(MemberStatus.Active, carolRow.Status);
        Assert.Equal(0, carolRow.AttendanceCount);
        Assert.Equal(0.0, carolRow.RatePercent ?? double.NaN);

        Assert.DoesNotContain(stats.Rows, r => r.UserId == expelledWithout); // 기간 출석이 없는 비활성은 제외
        Assert.Equal(66.7, stats.AverageRatePercent ?? double.NaN); // 2 / (3명 × 1회) = 66.66… → 66.7
    }

    [Fact]
    public async Task Stats_ReturnsNullRate_WhenNoActivityInRange()
    {
        await using var host = new ServiceHost();
        host.AsAdmin();
        await host.SeedUserAsync("김민수", "202512001");
        await host.SeedActivityAsync(new DateOnly(2026, 10, 1), ActivityStatus.ActivityClosed); // 조회 기간 밖

        var stats = await host.Attendance.GetAdminStatsAsync(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30));

        Assert.Equal(0, stats.ActivityCount);
        Assert.Equal(0, stats.AttendanceCount);
        Assert.Null(stats.AverageRatePercent);
        Assert.NotEmpty(stats.Rows);
        Assert.All(stats.Rows, r => Assert.Null(r.RatePercent));
    }

    [Fact]
    public async Task Stats_RoundsRateAwayFromZero_AtOneDecimal()
    {
        await using var host = new ServiceHost();
        host.AsAdmin();
        var alice = await host.SeedUserAsync("김민수", "202512001");

        // 16회 중 1회 = 6.25% — AwayFromZero면 6.3, ToEven이면 6.2로 갈리는 지점
        for (var day = 1; day <= 16; day++)
        {
            await host.SeedActivityAsync(new DateOnly(2026, 10, day), ActivityStatus.ActivityClosed);
        }
        await AddAttendanceDirectAsync(host, new DateOnly(2026, 10, 1), alice);

        var stats = await host.Attendance.GetAdminStatsAsync(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 16));

        Assert.Equal(16, stats.ActivityCount);
        Assert.Equal(6.3, stats.Rows.Single(r => r.UserId == alice).RatePercent ?? double.NaN);
        Assert.Equal(6.3, stats.AverageRatePercent ?? double.NaN);
    }

    [Fact]
    public async Task Stats_FallsBackToCurrentSemesterScope_WhenRangeOmitted()
    {
        await using var host = new ServiceHost();
        host.AsAdmin();
        await host.SeedUserAsync("김민수", "202512001");
        await host.SeedActivityAsync(new DateOnly(2026, 10, 1), ActivityStatus.ActivityClosed); // 기본 규칙(하반기 9/1~12/31) 안
        await host.SeedActivityAsync(new DateOnly(2026, 7, 1), ActivityStatus.ActivityClosed);  // 기본 규칙 밖

        var stats = await host.Attendance.GetAdminStatsAsync(null, null);

        Assert.Equal(new DateOnly(2026, 9, 1), stats.Start);
        Assert.Equal(new DateOnly(2026, 12, 31), stats.End);
        Assert.Equal(1, stats.ActivityCount);
    }

    private static async Task AddAttendanceDirectAsync(ServiceHost host, DateOnly date, string userId)    {
        await using var db = host.Db.CreateContext();
        var activity = await db.Activities.SingleAsync(a => a.ActivityDate == date);
        await host.SeedAttendanceAsync(userId, activity.Id);
    }
}
