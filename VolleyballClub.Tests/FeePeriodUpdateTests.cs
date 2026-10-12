using VolleyballClub.Application.Dtos;
using VolleyballClub.Application.Exceptions;
using Xunit;

namespace VolleyballClub.Tests;

/// <summary>회비 기간 수정 — 관리자 편집, 권한·검증 규칙.</summary>
public class FeePeriodUpdateTests
{
    [Fact]
    public async Task Admin_UpdatePeriod_UpdatesFields()
    {
        await using var host = new ServiceHost();
        var admin = await host.SeedUserAsync("김회장", "admin");
        host.AsAdmin(admin);
        var period = await host.Fee.CreatePeriodAsync(new FeePeriodCreateRequest(
            "2026년 2학기", new DateOnly(2026, 9, 1), new DateOnly(2026, 12, 31), 30_000));

        var updated = await host.Fee.UpdatePeriodAsync(period.Id, new FeePeriodUpdateRequest(
            "2026년 2학기(수정)", new DateOnly(2026, 9, 15), new DateOnly(2027, 1, 15), 35_000));

        Assert.Equal("2026년 2학기(수정)", updated.Name);
        Assert.Equal(new DateOnly(2026, 9, 15), updated.StartDate);
        Assert.Equal(new DateOnly(2027, 1, 15), updated.EndDate);
        Assert.Equal(35_000, updated.FeeAmount);

        var reloaded = await host.Fee.GetPeriodsAsync();
        Assert.Single(reloaded, p => p.Id == period.Id && p.FeeAmount == 35_000);
    }

    [Fact]
    public async Task Member_UpdatePeriod_ThrowsForbidden()
    {
        await using var host = new ServiceHost();
        var admin = await host.SeedUserAsync("김회장", "admin");
        var member = await host.SeedUserAsync("김민수", "202512001");

        host.AsAdmin(admin);
        var period = await host.Fee.CreatePeriodAsync(new FeePeriodCreateRequest(
            "2026년 2학기", new DateOnly(2026, 9, 1), new DateOnly(2026, 12, 31), 30_000));

        host.AsMember(member);
        await Assert.ThrowsAsync<ForbiddenException>(() => host.Fee.UpdatePeriodAsync(period.Id,
            new FeePeriodUpdateRequest("무단 수정", new DateOnly(2026, 9, 1), new DateOnly(2026, 12, 31), 0)));
    }

    [Fact]
    public async Task UpdatePeriod_StartAfterEnd_ThrowsRuleViolation()
    {
        await using var host = new ServiceHost();
        var admin = await host.SeedUserAsync("김회장", "admin");
        host.AsAdmin(admin);
        var period = await host.Fee.CreatePeriodAsync(new FeePeriodCreateRequest(
            "2026년 2학기", new DateOnly(2026, 9, 1), new DateOnly(2026, 12, 31), 30_000));

        await Assert.ThrowsAsync<RuleViolationException>(() => host.Fee.UpdatePeriodAsync(period.Id,
            new FeePeriodUpdateRequest("잘못된 기간", new DateOnly(2027, 1, 1), new DateOnly(2026, 12, 31), 30_000)));
    }

    [Fact]
    public async Task UpdatePeriod_MissingPeriod_ThrowsNotFound()
    {
        await using var host = new ServiceHost();
        var admin = await host.SeedUserAsync("김회장", "admin");
        host.AsAdmin(admin);

        await Assert.ThrowsAsync<NotFoundException>(() => host.Fee.UpdatePeriodAsync(999,
            new FeePeriodUpdateRequest("없는 기간", new DateOnly(2026, 9, 1), new DateOnly(2026, 12, 31), 30_000)));
    }
}
