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
        await host.Activity.StartActivityAsync();
        var otp = (await host.Activity.GetTodayForAdminAsync()).OtpCode!;

        foreach (var memberId in memberIds)
        {
            host.AsMember(memberId);
            await host.Attendance.CheckInWithOtpAsync(otp);
        }

        host.AsAdmin();
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
    public async Task Redraw_ReplacesPreviousDraw()
    {
        await GenerateAsync(3, 3);
        var redrawn = await GenerateAsync(2, 2, 2);

        // 다시 뽑기는 기존 팀을 지우고 새로 편성 — 이력 없이 현재 팀만 남는다
        Assert.Equal(3, redrawn.Count);
        var assigned = redrawn.SelectMany(t => t.Members).Select(m => m.UserId).ToList();
        Assert.Equal(6, assigned.Distinct().Count());

        await using var db = host.Db.CreateContext();
        Assert.Equal(3, await db.Teams.CountAsync());
        Assert.Equal(6, await db.TeamMembers.CountAsync());

        var adminView = await host.Team.GetTodayTeamsForAdminAsync();
        Assert.Equal(3, adminView.Count);
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
    public async Task DrawWhileActive_Succeeds()
    {
        // 출석 상시 오픈 정책 — 활동 중(팀 확정 전)에도 팀 편성이 가능하다
        var today = Domain.KstClock.Today(host.Time);
        await using var db = host.Db.CreateContext();
        var activity = await db.Activities.FirstAsync(a => a.ActivityDate == today);
        activity.Status = ActivityStatus.AttendanceOpen;
        await db.SaveChangesAsync();

        var teams = await GenerateAsync(2, 2, 2);
        Assert.Equal(3, teams.Count);
    }

    [Fact]
    public async Task Redraw_AfterConfirm_MemberSeesNewTeamsImmediately()
    {
        await GenerateAsync(2, 2, 2);
        await host.Team.ConfirmAsync();

        // 다시 뽑아도 상태는 팀 편성 완료로 유지 — 부원은 곧바로 새 팀을 본다
        await GenerateAsync(3, 3);

        host.AsMember(memberIds[0]);
        var latest = await host.Team.GetTodayTeamsAsync();
        Assert.Equal(2, latest.Count);
        Assert.Equal(6, latest.Sum(t => t.Members.Count));
    }

    [Fact]
    public async Task RemoveMember_RemovesFromTeam()
    {
        var teams = await GenerateAsync(2, 2, 2);
        var victim = teams[0].Members[0];

        await host.Team.RemoveMemberAsync(victim.TeamMemberId);

        var updated = await host.Team.GetTodayTeamsForAdminAsync();
        Assert.Equal(5, updated.Sum(t => t.Members.Count));
        Assert.DoesNotContain(updated.SelectMany(t => t.Members), m => m.TeamMemberId == victim.TeamMemberId);

        await using var db = host.Db.CreateContext();
        Assert.Equal(5, await db.TeamMembers.CountAsync());
    }

    [Fact]
    public async Task AddMember_AssignsMissingParticipant_AndRejectsDuplicate()
    {
        var teams = await GenerateAsync(3, 3);
        await host.Team.AddGuestAsync("늦게온친구");
        var guests = await host.Team.GetParticipantsAsync();
        var guest = guests.Participants.Single(p => p.IsGuest);

        await host.Team.AddMemberAsync(guest.Key, teams[0].TeamId);

        var updated = await host.Team.GetTodayTeamsForAdminAsync();
        Assert.Equal(4, updated[0].Members.Count);
        Assert.Contains(updated[0].Members, m => m.IsGuest);

        // 이미 팀에 배정된 게스트 재추가 거부
        await Assert.ThrowsAsync<RuleViolationException>(
            () => host.Team.AddMemberAsync(guest.Key, teams[1].TeamId));
    }

    [Fact]
    public async Task CancelTeams_RemovesAllTeams_AndUnpublishes()
    {
        await GenerateAsync(2, 2, 2);
        await host.Team.ConfirmAsync();

        await host.Team.CancelTeamsAsync();

        await using var db = host.Db.CreateContext();
        Assert.Equal(0, await db.Teams.CountAsync());
        Assert.Equal(0, await db.TeamMembers.CountAsync());

        var activity = await host.GetTodayActivityAsync();
        Assert.Equal(ActivityStatus.AttendanceOpen, activity.Status); // 부원 공개 철회

        host.AsMember(memberIds[0]);
        Assert.Empty(await host.Team.GetTodayTeamsAsync());

        // 편성이 없으면 취소 거부
        host.AsAdmin();
        await Assert.ThrowsAsync<RuleViolationException>(() => host.Team.CancelTeamsAsync());
    }

    /// <summary>마감하면 현재 팀은 삭제된다 — 팀 데이터는 진행 중 활동에만 존재.</summary>
    [Fact]
    public async Task CloseActivity_DeletesCurrentTeams()
    {
        await GenerateAsync(2, 2, 2);
        await host.Team.ConfirmAsync();
        await host.Activity.CloseActivityAsync();

        await using var db = host.Db.CreateContext();
        Assert.Equal(0, await db.Teams.CountAsync());
        Assert.Equal(0, await db.TeamMembers.CountAsync());

        var activity = await host.GetTodayActivityAsync();
        Assert.Equal(ActivityStatus.ActivityClosed, activity.Status);

        // 재오픈하면 출석 상태로 복귀(팀 없음)
        await host.Activity.ReopenActivityAsync();
        Assert.Equal(ActivityStatus.AttendanceOpen, (await host.Activity.GetTodayForAdminAsync()).Status);
    }
}
