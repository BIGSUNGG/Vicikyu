using Microsoft.EntityFrameworkCore;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Application.Exceptions;
using VolleyballClub.Domain.Enums;
using Xunit;

namespace VolleyballClub.Tests;

/// <summary>랜덤 팀 편성 — Fisher-Yates 배정, 합계 검증, 다시 뽑기, 팀원 이동, 확정.</summary>
public class TeamGenerationTests : IAsyncLifetime
{
    private readonly ServiceHost host = new();
    private readonly List<string> memberIds = [];

    public async Task InitializeAsync()
    {
        for (var i = 1; i <= 6; i++)
        {
            memberIds.Add(await host.SeedUserAsync($"부원{i}", $"2025120{i:00}"));
        }

        host.AsAdmin();
        await host.Activity.GetOrCreateTodayAsync();
        await host.Activity.GenerateOtpAsync();
        await host.Activity.StartAttendanceAsync();
        var otp = (await host.Activity.GetTodayForAdminAsync()).OtpCode!;

        foreach (var memberId in memberIds)
        {
            host.AsMember(memberId);
            await host.Attendance.CheckInWithOtpAsync(otp);
        }

        host.AsAdmin();
        await host.Activity.CloseAttendanceAsync();
    }

    public Task DisposeAsync() => host.DisposeAsync().AsTask();

    private Task<List<TeamResultDto>> GenerateAsync(params int[] sizes) =>
        host.Team.GenerateAsync(new TeamCreateRequest([.. memberIds], [], [.. sizes]));

    [Fact]
    public async Task SizeSumMismatch_ThrowsWithMessage()
    {
        var ex = await Assert.ThrowsAsync<RuleViolationException>(() => GenerateAsync(3, 2));
        Assert.Contains("참가 인원 6명과 일치하지 않습니다", ex.Message);
    }

    [Fact]
    public async Task Generate_AssignsAllParticipantsExactlyOnceWithExactSizes()
    {
        var teams = await GenerateAsync(2, 2, 2);

        Assert.Equal(3, teams.Count);
        Assert.Equal(new[] { "A TEAM", "B TEAM", "C TEAM" }, teams.Select(t => t.Name));
        Assert.All(teams, t => Assert.Equal(2, t.Members.Count));

        var assigned = teams.SelectMany(t => t.Members).Select(m => m.UserId).ToList();
        Assert.Equal(6, assigned.Count);
        Assert.Equal(memberIds.OrderBy(x => x), assigned.OrderBy(x => x)); // 중복 없이 전원 1회씩
    }

    [Fact]
    public async Task Redraw_ReplacesPreviousTeams()
    {
        await GenerateAsync(3, 3);
        var redrawn = await GenerateAsync(2, 2, 2);

        Assert.Equal(3, redrawn.Count);
        var assigned = redrawn.SelectMany(t => t.Members).Select(m => m.UserId).ToList();
        Assert.Equal(6, assigned.Distinct().Count());

        await using var db = host.Db.CreateContext();
        Assert.Equal(3, await db.Teams.CountAsync());
        Assert.Equal(6, await db.TeamMembers.CountAsync());
    }

    [Fact]
    public async Task MoveMember_TransfersToTargetTeam()
    {
        var teams = await GenerateAsync(2, 2, 2);
        var firstMember = teams[0].Members[0];
        var targetTeam = teams[1];

        await host.Team.MoveMemberAsync(firstMember.TeamMemberId, targetTeam.TeamId);

        var updated = await host.Team.GetTodayTeamsForAdminAsync();
        Assert.DoesNotContain(updated[0].Members, m => m.TeamMemberId == firstMember.TeamMemberId);
        Assert.Contains(updated[1].Members, m => m.TeamMemberId == firstMember.TeamMemberId);
        Assert.Equal(6, updated.SelectMany(t => t.Members).Count());
    }

    [Fact]
    public async Task Confirm_SetsStatusAndExposesTeamsToMembers()
    {
        await GenerateAsync(2, 2, 2);

        // 확정 전에는 부원에게 팀이 보이지 않는다
        host.AsMember(memberIds[0]);
        Assert.Empty(await host.Team.GetTodayTeamsAsync());

        host.AsAdmin();
        await host.Team.ConfirmAsync();

        host.AsMember(memberIds[0]);
        var teams = await host.Team.GetTodayTeamsAsync();
        Assert.Equal(3, teams.Count);

        var activity = await host.GetTodayActivityAsync();
        Assert.Equal(ActivityStatus.TeamCreated, activity.Status);
    }

    [Fact]
    public async Task DrawBeforeClosingAttendance_Throws()
    {
        var today = Domain.KstClock.Today(host.Time);
        await using var db = host.Db.CreateContext();
        var activity = await db.Activities.FirstAsync(a => a.ActivityDate == today);
        activity.Status = ActivityStatus.AttendanceOpen;
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<ConflictException>(() => GenerateAsync(2, 2, 2));
    }
}
