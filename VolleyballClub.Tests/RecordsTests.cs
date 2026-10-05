using Microsoft.EntityFrameworkCore;
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
        Assert.Empty(records.Records[0].Teams);

        var first = records.Records[1];
        Assert.Equal(new DateOnly(2026, 10, 1), first.Date);
        Assert.Equal(ActivityStatus.TeamCreated, first.Status);
        Assert.True(first.IsAttended);
        Assert.NotNull(first.CheckedAt);
        var assignment = Assert.Single(first.Teams);
        Assert.Equal(1, assignment.DrawNumber);
        Assert.Equal("B팀", assignment.TeamName);
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
        Assert.Empty(mine.Records.Single().Teams);
        Assert.Equal(0, mine.MonthCount);

        Assert.True(bobs.Records.Single().IsAttended);
        Assert.Equal("A팀", bobs.Records.Single().Teams.Single().TeamName);
        Assert.Equal(1, bobs.MonthCount);
    }

    [Fact]
    public async Task Records_IncludeAllConfirmedDraws_AndExcludeUnconfirmed()
    {
        await using var host = new ServiceHost();
        var alice = await host.SeedUserAsync("김민수", "202512001");

        var activity = await host.SeedActivityAsync(new DateOnly(2026, 10, 1), ActivityStatus.TeamCreated);
        await using (var db = host.Db.CreateContext())
        {
            var a = await db.Activities.SingleAsync(x => x.Id == activity.Id);
            a.ConfirmedDrawNumber = 2; // 1·2회차 확정, 3회차는 아직 미확정
            await db.SaveChangesAsync();
        }

        await AddAttendanceDirectAsync(host, activity.Id, alice);
        foreach (var (draw, teamName) in new[] { (1, "A팀"), (2, "B팀"), (3, "C팀") })
        {
            await AddTeamMemberDirectAsync(host, activity.Id, teamName, alice, draw);
        }

        var record = (await host.Attendance.GetMyRecordsAsync(alice, 2026, 10)).Records.Single();

        // 확정된 1·2회차만, 회차 순서대로
        Assert.Equal(2, record.Teams.Count);
        Assert.Equal(new[] { 1, 2 }, record.Teams.Select(t => t.DrawNumber));
        Assert.Equal(new[] { "A팀", "B팀" }, record.Teams.Select(t => t.TeamName));
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

    private static async Task AddTeamMemberDirectAsync(ServiceHost host, int activityId, string teamName, string userId, int drawNumber = 1)
    {
        await using var db = host.Db.CreateContext();
        var team = new Team { ActivityId = activityId, Name = teamName, DrawNumber = drawNumber, CreatedAt = DateTime.UtcNow };
        db.Teams.Add(team);
        await db.SaveChangesAsync();
        db.TeamMembers.Add(new TeamMember { TeamId = team.Id, UserId = userId, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }
}
