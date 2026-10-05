using VolleyballClub.Domain.Entities;
using VolleyballClub.Domain.Enums;
using Xunit;

namespace VolleyballClub.Tests;

/// <summary>기록 페이지용 월간 이력 조회 — 활동 전체(상태 포함)·내 출석·배정 팀 묶음.</summary>
public class RecordsTests
{
    [Fact]
    public async Task Records_ListsAllMonthActivities_LatestFirst_WithEvents()
    {
        await using var host = new ServiceHost();
        var alice = await host.SeedUserAsync("김민수", "202512001");

        // 다른 달 활동은 제외
        await host.SeedActivityAsync(new DateOnly(2026, 9, 30), ActivityStatus.ActivityClosed);

        // 10/1: 팀 편성 완료 — alice 출석 + B팀 배정
        var oct1 = await host.SeedActivityAsync(new DateOnly(2026, 10, 1), ActivityStatus.TeamCreated);
        await AddAttendanceDirectAsync(host, oct1.Id, alice);
        await AddTeamMemberDirectAsync(host, oct1.Id, "B팀", alice);

        // 10/2: 종료된 활동 — alice 미출석
        await host.SeedActivityAsync(new DateOnly(2026, 10, 2), ActivityStatus.ActivityClosed);

        var records = await host.Attendance.GetMyRecordsAsync(alice, 2026, 10);

        Assert.Equal(2, records.MonthActivities);
        Assert.Equal(1, records.MonthCount);
        Assert.Equal(2, records.Records.Count);
        Assert.Equal(new DateOnly(2026, 10, 2), records.Records[0].Date); // 최신순
        Assert.Equal(ActivityStatus.ActivityClosed, records.Records[0].Status);
        Assert.False(records.Records[0].IsAttended);
        Assert.Null(records.Records[0].TeamName);

        var first = records.Records[1];
        Assert.Equal(new DateOnly(2026, 10, 1), first.Date);
        Assert.Equal(ActivityStatus.TeamCreated, first.Status);
        Assert.True(first.IsAttended);
        Assert.NotNull(first.CheckedAt);
        Assert.Equal("B팀", first.TeamName);
    }

    [Fact]
    public async Task Records_AreScopedToRequestingUser()
    {
        await using var host = new ServiceHost();
        var alice = await host.SeedUserAsync("김민수", "202512001");
        var bob = await host.SeedUserAsync("박지훈", "202512002");

        var activity = await host.SeedActivityAsync(new DateOnly(2026, 10, 1), ActivityStatus.ActivityClosed);
        await AddAttendanceDirectAsync(host, activity.Id, bob);
        await AddTeamMemberDirectAsync(host, activity.Id, "A팀", bob);

        var mine = await host.Attendance.GetMyRecordsAsync(alice, 2026, 10);
        var bobs = await host.Attendance.GetMyRecordsAsync(bob, 2026, 10);

        Assert.False(mine.Records.Single().IsAttended);
        Assert.Null(mine.Records.Single().TeamName);
        Assert.Equal(0, mine.MonthCount);

        Assert.True(bobs.Records.Single().IsAttended);
        Assert.Equal("A팀", bobs.Records.Single().TeamName);
        Assert.Equal(1, bobs.MonthCount);
    }

    private static async Task AddAttendanceDirectAsync(ServiceHost host, int activityId, string userId)
    {
        await using var db = host.Db.CreateContext();
        db.Attendances.Add(new Attendance
        {
            ActivityId = activityId,
            UserId = userId,
            AttendanceType = AttendanceType.Otp,
            CheckedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task AddTeamMemberDirectAsync(ServiceHost host, int activityId, string teamName, string userId)
    {
        await using var db = host.Db.CreateContext();
        var team = new Team { ActivityId = activityId, Name = teamName, CreatedAt = DateTime.UtcNow };
        db.Teams.Add(team);
        await db.SaveChangesAsync();
        db.TeamMembers.Add(new TeamMember { TeamId = team.Id, UserId = userId, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }
}
