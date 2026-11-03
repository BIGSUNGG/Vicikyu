using Microsoft.EntityFrameworkCore;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Application.Exceptions;
using VolleyballClub.Domain.Enums;
using Xunit;

namespace VolleyballClub.Tests;

/// <summary>게스트 제거 — 팀 배치까지 정리되고, 종료된 활동에서는 거부된다.</summary>
public sealed class GuestRemovalTests : IAsyncLifetime
{
    private readonly ServiceHost host = new();
    private readonly List<string> memberIds = [];

    public async Task InitializeAsync()
    {
        for (var i = 1; i <= 4; i++)
            memberIds.Add(await host.SeedUserAsync($"부원{i}", $"2025120{i:00}"));

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

    [Fact]
    public async Task RemoveGuest_ClearsTeamPlacementAndGuest()
    {
        await host.Team.GenerateAsync(new TeamCreateRequest([.. memberIds], [], [2, 2]));
        await host.Team.AddGuestAsync("늦게온친구");
        var guest = (await host.Team.GetParticipantsAsync()).Participants.Single(p => p.IsGuest);
        await host.Team.AddMemberAsync(guest.Key, (await host.Team.GetTodayTeamsForAdminAsync())[0].TeamId);

        await host.Team.RemoveGuestAsync(guest.GuestId!.Value);

        var updated = await host.Team.GetTodayTeamsForAdminAsync();
        Assert.Equal(2, updated[0].Members.Count); // 게스트 자리 제거로 원상 복귀
        Assert.DoesNotContain((await host.Team.GetParticipantsAsync()).Participants, p => p.IsGuest);

        await using var db = host.Db.CreateContext();
        Assert.Equal(4, await db.TeamMembers.CountAsync());
        Assert.Equal(0, await db.Guests.CountAsync());
        var log = await db.AuditLogs.AsNoTracking().SingleAsync(l => l.Action == AdminAction.GuestRemove);
        Assert.Contains("늦게온친구", log.Details);
    }

    [Fact]
    public async Task RemoveGuest_OnClosedActivity_ThrowsConflict()
    {
        await host.Team.AddGuestAsync("게스트");
        var guest = (await host.Team.GetParticipantsAsync()).Participants.Single(p => p.IsGuest);
        await host.Activity.CloseActivityAsync();

        await Assert.ThrowsAsync<ConflictException>(() => host.Team.RemoveGuestAsync(guest.GuestId!.Value));
    }

    [Fact]
    public async Task RemoveGuest_UnknownId_ThrowsNotFound()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => host.Team.RemoveGuestAsync(999));
    }
}
