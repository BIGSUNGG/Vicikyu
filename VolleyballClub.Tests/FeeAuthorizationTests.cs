using VolleyballClub.Application.Dtos;
using VolleyballClub.Application.Exceptions;
using Xunit;

namespace VolleyballClub.Tests;

/// <summary>회비 상태 변경 권한 — 일반 부원의 서버 레벨 차단.</summary>
public class FeeAuthorizationTests
{
    [Fact]
    public async Task Member_SetPaid_ThrowsForbidden()
    {
        await using var host = new ServiceHost();
        var admin = await host.SeedUserAsync("김회장", "admin");
        var member = await host.SeedUserAsync("김민수", "202512001");

        host.AsAdmin(admin);
        var period = await host.Fee.CreatePeriodAsync(new FeePeriodCreateRequest(
            "2026년 2학기", new DateOnly(2026, 9, 1), new DateOnly(2026, 12, 31), 30_000));

        // 일반 부원이 자신(또는 타인)의 납부 상태를 바꾸려 하면 서버에서 차단
        host.AsMember(member);
        var ex = await Assert.ThrowsAsync<ForbiddenException>(
            () => host.Fee.SetPaidAsync(period.Id, member, isPaid: true));
        Assert.Contains("관리자", ex.Message);

        // 상태도 바뀌지 않았다
        host.AsAdmin(admin);
        var statuses = await host.Fee.GetPeriodStatusesAsync(period.Id);
        Assert.False(statuses.Single(s => s.UserId == member).IsPaid);
    }

    [Fact]
    public async Task Member_GetPeriodStatuses_ThrowsForbidden()
    {
        await using var host = new ServiceHost();
        var member = await host.SeedUserAsync("박지훈", "202512002");
        host.AsMember(member);

        await Assert.ThrowsAsync<ForbiddenException>(() => host.Fee.GetPeriodStatusesAsync(1));
    }
}
