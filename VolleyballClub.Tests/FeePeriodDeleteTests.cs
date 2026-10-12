using VolleyballClub.Application.Dtos;
using VolleyballClub.Application.Exceptions;
using Xunit;

namespace VolleyballClub.Tests;

/// <summary>회비 기간 삭제 — 납부 기록 보호 규칙 중심.</summary>
public class FeePeriodDeleteTests
{
    [Fact]
    public async Task Admin_DeletePeriod_WithoutPayments_Deletes()
    {
        await using var host = new ServiceHost();
        var admin = await host.SeedUserAsync("김회장", "admin");
        host.AsAdmin(admin);
        var period = await host.Fee.CreatePeriodAsync(new FeePeriodCreateRequest(
            "잘못 만든 기간", new DateOnly(2026, 9, 1), new DateOnly(2026, 12, 31), 30_000));

        await host.Fee.DeletePeriodAsync(period.Id);

        var periods = await host.Fee.GetPeriodsAsync();
        Assert.DoesNotContain(periods, p => p.Id == period.Id);
    }

    [Fact]
    public async Task Admin_DeletePeriod_WithPaidMember_Blocked()
    {
        await using var host = new ServiceHost();
        var admin = await host.SeedUserAsync("김회장", "admin");
        var member = await host.SeedUserAsync("김민수", "202512001");

        host.AsAdmin(admin);
        var period = await host.Fee.CreatePeriodAsync(new FeePeriodCreateRequest(
            "2026년 2학기", new DateOnly(2026, 9, 1), new DateOnly(2026, 12, 31), 30_000));
        await host.Fee.SetPaidAsync(period.Id, member, isPaid: true);

        var ex = await Assert.ThrowsAsync<RuleViolationException>(() => host.Fee.DeletePeriodAsync(period.Id));
        Assert.Contains("납부 처리된 부원", ex.Message);

        // 기간도 납부 상태도 그대로 존재한다
        var periods = await host.Fee.GetPeriodsAsync();
        Assert.Single(periods, p => p.Id == period.Id);
        var statuses = await host.Fee.GetPeriodStatusesAsync(period.Id);
        Assert.True(statuses.Single(s => s.UserId == member).IsPaid);
    }

    [Fact]
    public async Task Admin_DeletePeriod_WithUnpaidRecordOnly_Deletes()
    {
        await using var host = new ServiceHost();
        var admin = await host.SeedUserAsync("김회장", "admin");
        var member = await host.SeedUserAsync("박지훈", "202512002");

        host.AsAdmin(admin);
        var period = await host.Fee.CreatePeriodAsync(new FeePeriodCreateRequest(
            "미납 이력만 있는 기간", new DateOnly(2026, 9, 1), new DateOnly(2026, 12, 31), 30_000));
        // 미납 처리만 해둔 상태 — 행은 생성되지만 IsPaid는 false
        await host.Fee.SetPaidAsync(period.Id, member, isPaid: false);

        await host.Fee.DeletePeriodAsync(period.Id);

        var periods = await host.Fee.GetPeriodsAsync();
        Assert.DoesNotContain(periods, p => p.Id == period.Id);
    }

    [Fact]
    public async Task Member_DeletePeriod_ThrowsForbidden()
    {
        await using var host = new ServiceHost();
        var admin = await host.SeedUserAsync("김회장", "admin");
        var member = await host.SeedUserAsync("박지훈", "202512002");

        host.AsAdmin(admin);
        var period = await host.Fee.CreatePeriodAsync(new FeePeriodCreateRequest(
            "2026년 2학기", new DateOnly(2026, 9, 1), new DateOnly(2026, 12, 31), 30_000));

        host.AsMember(member);
        await Assert.ThrowsAsync<ForbiddenException>(() => host.Fee.DeletePeriodAsync(period.Id));
    }

    [Fact]
    public async Task DeletePeriod_MissingPeriod_ThrowsNotFound()
    {
        await using var host = new ServiceHost();
        var admin = await host.SeedUserAsync("김회장", "admin");
        host.AsAdmin(admin);

        await Assert.ThrowsAsync<NotFoundException>(() => host.Fee.DeletePeriodAsync(999));
    }
}
