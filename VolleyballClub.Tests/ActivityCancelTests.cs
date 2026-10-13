using Microsoft.EntityFrameworkCore;
using VolleyballClub.Application.Exceptions;
using VolleyballClub.Domain.Entities;
using VolleyballClub.Domain.Enums;

namespace VolleyballClub.Tests;

/// <summary>활동 취소(하드 삭제) — 활동 없는 날 실수로 시작했을 때 되돌리기.</summary>
public class ActivityCancelTests : IAsyncLifetime
{
    private readonly ServiceHost host = new();

    public async Task InitializeAsync()
    {
        await host.SeedUserAsync("박지훈", "202512002");
    }

    public Task DisposeAsync() => host.DisposeAsync().AsTask();

    [Fact]
    public async Task CancelActivity_WithRecords_DeletesAllAndLogs()
    {
        host.AsAdmin();
        var activity = await host.SeedActivityAsync(Domain.KstClock.Today(host.Time), ActivityStatus.AttendanceOpen, otp: "1234");
        await SeedAttendanceAsync(activity.Id, "user-202512002");
        await SeedGuestAsync(activity.Id);
        await SeedTeamAsync(activity.Id);

        var cancelledId = host.GetTodayActivityAsync().Id;

        await host.Activity.CancelActivityAsync();

        Assert.Contains(cancelledId, host.SheetSync.CancelledActivityIds); // 시트 출석 정보 제거 호출

        await using var db = host.Db.CreateContext();
        Assert.Equal(0, await db.Activities.CountAsync());
        Assert.Equal(0, await db.Attendances.CountAsync());
        Assert.Equal(0, await db.Guests.CountAsync());
        Assert.Equal(0, await db.Teams.CountAsync());
        Assert.Equal(0, await db.TeamMembers.CountAsync());
        var log = await db.AuditLogs.AsNoTracking().SingleAsync(l => l.Action == AdminAction.ActivityCancel);
        Assert.Contains("출석 1", log.Details);
        Assert.Contains("팀 1", log.Details);

        host.AsAdmin();
        await Assert.ThrowsAsync<NotFoundException>(() => host.Activity.GetTodayForAdminAsync());
    }

    [Fact]
    public async Task CancelActivity_FromClosed_Throws()
    {
        host.AsAdmin();
        await host.Activity.GetOrCreateTodayAsync();
        await host.Activity.StartActivityAsync();
        await host.Activity.CloseActivityAsync();

        var ex = await Assert.ThrowsAsync<ConflictException>(() => host.Activity.CancelActivityAsync());
        Assert.Contains("취소할 수 없", ex.Message);
    }

    private async Task SeedAttendanceAsync(int activityId, string userId)
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

    private async Task SeedGuestAsync(int activityId)
    {
        await using var db = host.Db.CreateContext();
        db.Guests.Add(new Guest { ActivityId = activityId, Name = "게스트1", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    private async Task SeedTeamAsync(int activityId)
    {
        await using var db = host.Db.CreateContext();
        var team = new Team { ActivityId = activityId, Name = "A TEAM", Order = 0, CreatedAt = DateTime.UtcNow };
        db.Teams.Add(team);
        db.TeamMembers.Add(new TeamMember { Team = team, UserId = "user-202512002", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }
}
