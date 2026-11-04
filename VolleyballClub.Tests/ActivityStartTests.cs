using Microsoft.EntityFrameworkCore;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Domain.Enums;

namespace VolleyballClub.Tests;

/// <summary>활동 시작 전 행 비저장 — 조회는 가상 상태만 돌려주고, 시작 시에만 행이 생긴다.</summary>
public sealed class ActivityStartTests
{
    /// <summary>빈 DB에서 관리자 오늘 조회 — NotFound가 아니라 가상 '준비 중' DTO, 행도 만들지 않는다.</summary>
    [Fact]
    public async Task GetTodayForAdmin_OnEmptyDb_ReturnsVirtualReadyWithoutRow()
    {
        await using var host = new ServiceHost();
        var member = await host.SeedUserAsync("민수", "202610001");
        host.AsAdmin();

        var today = await host.Activity.GetTodayForAdminAsync();

        Assert.Equal(0, today.ActivityId);
        Assert.Null(today.Status);
        Assert.Equal("준비 중", today.StatusText);
        Assert.Null(today.OtpCode);
        Assert.Equal(0, today.AttendanceCount);
        Assert.Equal(1, today.TotalActiveMembers); // Active 회원 수는 실제로 조회한다
        await using var db = host.Db.CreateContext();
        Assert.Equal(0, await db.Activities.CountAsync()); // 조회만으로 행이 생기지 않는다
    }

    /// <summary>StartNewActivityAsync — 오늘 날짜 AttendanceOpen 행 생성, OTP 자동 생성, 당일 예정 표시 제거.</summary>
    [Fact]
    public async Task StartNewActivity_CreatesOpenRowWithOtpAndRemovesScheduledMark()
    {
        await using var host = new ServiceHost();
        host.AsAdmin();
        var today = Domain.KstClock.Today(host.Time);
        await host.AdminActivities.CreateAsync(new ActivityCreateRequest(today, DayKind.Scheduled));

        var started = await host.Activity.StartNewActivityAsync();

        Assert.Equal(ActivityStatus.AttendanceOpen, started.Status);
        Assert.Equal(today, started.ActivityDate);
        Assert.Matches("^\\d{4}$", started.OtpCode);
        await using var db = host.Db.CreateContext();
        var row = await db.Activities.SingleAsync();
        Assert.Equal(ActivityStatus.AttendanceOpen, row.Status);
        Assert.Equal(started.OtpCode, row.OtpCode);
        Assert.Empty(await db.DayMarks.Where(m => m.Date == today).ToListAsync()); // 당일 예정 표시 제거
    }

    /// <summary>부원 홈 — 시작 전 예정 표시면 가상 '활동 예정', 표시 없음·'활동 없음'이면 null.</summary>
    [Fact]
    public async Task GetMyToday_BeforeStart_ShowsScheduledOrNull()
    {
        await using var host = new ServiceHost();
        var member = await host.SeedUserAsync("민수", "202610001");
        var today = Domain.KstClock.Today(host.Time);

        Assert.Null(await host.Activity.GetMyTodayAsync(member)); // 표시 없음 → 일정 없음

        host.AsAdmin();
        await host.AdminActivities.CreateAsync(new ActivityCreateRequest(today, DayKind.NoActivity));
        Assert.Null(await host.Activity.GetMyTodayAsync(member)); // '활동 없음' 표시도 일정 없음

        await host.AdminActivities.CreateAsync(new ActivityCreateRequest(today, DayKind.Scheduled));
        var myToday = await host.Activity.GetMyTodayAsync(member);

        Assert.NotNull(myToday);
        Assert.Null(myToday.Status);
        Assert.Equal("활동 예정", myToday.StatusText);
        Assert.Equal(today, myToday.ActivityDate);
        Assert.False(myToday.IsAttended);
        Assert.Equal(0, myToday.AttendanceCount);
    }
}
