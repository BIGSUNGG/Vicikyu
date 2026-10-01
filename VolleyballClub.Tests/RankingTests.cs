using Microsoft.EntityFrameworkCore;
using VolleyballClub.Domain.Enums;
using Xunit;

namespace VolleyballClub.Tests;

/// <summary>출석왕 랭킹 — 집계, 공동 순위, 기간 필터(월/학기).</summary>
public class RankingTests
{
    [Fact]
    public async Task Ranking_CountsAttendances_AndGivesCoRankToTies()
    {
        await using var host = new ServiceHost();
        var alice = await host.SeedUserAsync("김민수", "202512001");
        var bob = await host.SeedUserAsync("박지훈", "202512002");
        var carol = await host.SeedUserAsync("이정우", "202512003");

        // 9월 30일(2학기, 지난달) 활동: alice, bob 출석
        await host.SeedActivityAsync(new DateOnly(2026, 9, 30), ActivityStatus.ActivityClosed);
        await AddAttendanceDirectAsync(host, new DateOnly(2026, 9, 30), alice);
        await AddAttendanceDirectAsync(host, new DateOnly(2026, 9, 30), bob);

        // 10월 1일(오늘) 활동: alice, bob, carol 출석
        await host.SeedActivityAsync(new DateOnly(2026, 10, 1), ActivityStatus.TeamCreated);
        await AddAttendanceDirectAsync(host, new DateOnly(2026, 10, 1), alice);
        await AddAttendanceDirectAsync(host, new DateOnly(2026, 10, 1), bob);
        await AddAttendanceDirectAsync(host, new DateOnly(2026, 10, 1), carol);

        var all = await host.Attendance.GetRankingAsync(RankingPeriod.All);
        Assert.Equal(3, all.Count);
        Assert.Equal(2, all[0].Count); // alice·bob 공동 1위
        Assert.Equal(1, all[0].Rank);
        Assert.Equal(1, all[1].Rank);  // 공동 순위
        Assert.Equal(3, all[2].Rank);  // 경쟁 순위: 다음은 3위
        Assert.Equal(1, all[2].Count);

        var month = await host.Attendance.GetRankingAsync(RankingPeriod.Month);
        Assert.All(month, r => Assert.Equal(1, r.Count));

        var semester = await host.Attendance.GetRankingAsync(RankingPeriod.Semester);
        Assert.Equal(3, semester.Count);

        // 조회자 하이라이트 플래그
        var mine = await host.Attendance.GetRankingAsync(RankingPeriod.All, carol);
        Assert.True(mine.Single(r => r.UserId == carol).IsViewer);
    }

    [Fact]
    public async Task Ranking_ExcludesReadyActivities_FromAllPeriods()
    {
        await using var host = new ServiceHost();
        var alice = await host.SeedUserAsync("김민수", "202512001");

        // Ready 상태(개최되지 않은) 활동의 출석은 집계에서 제외
        await host.SeedActivityAsync(new DateOnly(2026, 10, 1), ActivityStatus.Ready);
        await AddAttendanceDirectAsync(host, new DateOnly(2026, 10, 1), alice);

        var all = await host.Attendance.GetRankingAsync(RankingPeriod.All);
        Assert.Empty(all);
    }

    private static async Task AddAttendanceDirectAsync(ServiceHost host, DateOnly date, string userId)
    {
        await using var db = host.Db.CreateContext();
        var activity = await db.Activities.SingleAsync(a => a.ActivityDate == date);
        db.Attendances.Add(new VolleyballClub.Domain.Entities.Attendance
        {
            ActivityId = activity.Id,
            UserId = userId,
            AttendanceType = AttendanceType.Otp,
            CheckedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }
}
