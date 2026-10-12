using VolleyballClub.Application.Exceptions;
using VolleyballClub.Domain.Enums;

namespace VolleyballClub.Tests;

/// <summary>활동 종료는 마감 시점 기준 — 자정이 지나도 마감 전까지 활동은 계속 진행된다.</summary>
public sealed class MidnightCloseTests
{
    [Fact]
    public async Task UnclosedActivity_ContinuesPastMidnight()
    {
        await using var host = new ServiceHost(); // 2026-10-01 18:00 KST
        host.AsAdmin();
        var day1 = await host.Activity.GetOrCreateTodayAsync();
        await host.Activity.StartActivityAsync();
        var otp = (await host.Activity.GetTodayForAdminAsync()).OtpCode!;
        var member = await host.SeedUserAsync("민수", "202610001");

        host.Time.MoveTo(new DateOnly(2026, 10, 2), 9); // 다음 날로 경과

        host.AsMember(member);
        await host.Attendance.CheckInWithOtpAsync(otp); // 자정이 지나도 마감 전 출석 가능

        host.AsAdmin();
        var current = await host.Activity.GetOrCreateTodayAsync(); // 새 활동 생성 없이 진행 중 활동 반환
        Assert.Equal(day1.ActivityId, current.ActivityId);
        Assert.Equal(new DateOnly(2026, 10, 1), current.ActivityDate);

        await host.Activity.CloseActivityAsync(); // 마감을 누른 시점에 종료

        host.AsMember(member);
        await Assert.ThrowsAsync<ConflictException>(() => host.Attendance.CheckInWithOtpAsync(otp));
    }

    [Fact]
    public async Task AfterClose_NewDay_CreatesNewActivity()
    {
        await using var host = new ServiceHost();
        host.AsAdmin();
        var day1 = await host.Activity.GetOrCreateTodayAsync();
        await host.Activity.StartActivityAsync();
        await host.Activity.CloseActivityAsync();

        host.Time.MoveTo(new DateOnly(2026, 10, 2), 9);
        var day2 = await host.Activity.GetOrCreateTodayAsync(); // 진행 중 활동 없음 → 새 활동 생성
        Assert.NotEqual(day1.ActivityId, day2.ActivityId);
        Assert.Equal(new DateOnly(2026, 10, 2), day2.ActivityDate);
        Assert.Equal(ActivityStatus.Ready, day2.Status);
    }
}
