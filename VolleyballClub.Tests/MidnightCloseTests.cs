using Microsoft.EntityFrameworkCore;
using VolleyballClub.Application.Exceptions;

namespace VolleyballClub.Tests;

/// <summary>활동 종료는 마감 시점 기준 — 자정이 지나도 마감 전까지 활동은 계속 진행된다.</summary>
public sealed class MidnightCloseTests
{
    [Fact]
    public async Task UnclosedActivity_ContinuesPastMidnight()
    {
        await using var host = new ServiceHost(); // 2026-10-01 18:00 KST
        host.AsAdmin();
        var day1 = await host.Activity.StartNewActivityAsync();
        var otp = day1.OtpCode!;
        var member = await host.SeedUserAsync("민수", "202610001");

        host.Time.MoveTo(new DateOnly(2026, 10, 2), 9); // 다음 날로 경과

        host.AsMember(member);
        await host.Attendance.CheckInWithOtpAsync(otp); // 자정이 지나도 마감 전 출석 가능

        host.AsAdmin();
        var current = await host.Activity.GetTodayForAdminAsync(); // 새 활동 생성 없이 진행 중 활동 반환
        Assert.Equal(day1.ActivityId, current.ActivityId);
        Assert.Equal(new DateOnly(2026, 10, 1), current.ActivityDate);

        await host.Activity.CloseActivityAsync(); // 마감을 누른 시점에 종료

        host.AsMember(member);
        await Assert.ThrowsAsync<ConflictException>(() => host.Attendance.CheckInWithOtpAsync(otp));
    }

    /// <summary>다음 날 조회는 가상 '준비 중'만 돌려주고 활동 행을 만들지 않는다.</summary>
    [Fact]
    public async Task AfterClose_NewDay_ReturnsVirtualReadyWithoutCreatingRow()
    {
        await using var host = new ServiceHost();
        host.AsAdmin();
        await host.Activity.StartNewActivityAsync();
        await host.Activity.CloseActivityAsync();

        host.Time.MoveTo(new DateOnly(2026, 10, 2), 9);
        var day2 = await host.Activity.GetTodayForAdminAsync();
        Assert.Null(day2.Status); // 행 없는 가상 '준비 중'
        Assert.Equal("준비 중", day2.StatusText);
        Assert.Equal(new DateOnly(2026, 10, 2), day2.ActivityDate);
        await using var db = host.Db.CreateContext();
        Assert.Equal(1, await db.Activities.CountAsync()); // 어제 마감 세션만 남는다
    }
}
