using Microsoft.EntityFrameworkCore;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Application.Exceptions;
using VolleyballClub.Domain.Entities;
using VolleyballClub.Domain.Enums;

namespace VolleyballClub.Tests;

/// <summary>마감 후 새 세션 시작 — 이전 세션 기록은 유지되고 별도 출석이 가능하다.</summary>
public sealed class StartNewActivityTests
{
    [Fact]
    public async Task ClosedActivity_StartNew_CreatesSeparateSession()
    {
        await using var host = new ServiceHost();
        host.AsAdmin();
        var session1 = await host.Activity.GetOrCreateTodayAsync();
        await host.Activity.StartActivityAsync();
        Assert.Contains(session1.ActivityId, host.SheetSync.ScheduledActivityIds); // 시작 → 신형 탭 예정 기록 호출
        var otp1 = (await host.Activity.GetTodayForAdminAsync()).OtpCode!;
        var member = await host.SeedUserAsync("민수", "202610001");
        host.AsMember(member);
        await host.Attendance.CheckInWithOtpAsync(otp1); // 1세션 출석

        host.AsAdmin();
        await host.Activity.CloseActivityAsync();
        var session2 = await host.Activity.StartNewActivityAsync();
        Assert.Contains(session2.ActivityId, host.SheetSync.ScheduledActivityIds); // 새 세션 시작 → 예정 기록 호출

        Assert.NotEqual(session1.ActivityId, session2.ActivityId);
        Assert.Equal(session1.ActivityDate, session2.ActivityDate); // 같은 날 새 세션
        Assert.Equal(ActivityStatus.AttendanceOpen, session2.Status);
        Assert.NotNull(session2.OtpCode);
        Assert.Equal(0, session2.AttendanceCount); // 이전 출석과 별도

        host.AsMember(member);
        await host.Attendance.CheckInWithOtpAsync(session2.OtpCode!); // 새 세션 별도 출석

        await using var db = host.Db.CreateContext();
        Assert.Equal(1, await db.Attendances.CountAsync(a => a.ActivityId == session1.ActivityId));
        Assert.Equal(1, await db.Attendances.CountAsync(a => a.ActivityId == session2.ActivityId));

        host.AsAdmin();
        Assert.Equal(session2.ActivityId, (await host.Activity.GetOrCreateTodayAsync()).ActivityId); // 현재 활동 = 새 세션
    }

    [Fact]
    public async Task ActiveActivity_StartNew_ThrowsConflict()
    {
        await using var host = new ServiceHost();
        host.AsAdmin();
        await host.Activity.GetOrCreateTodayAsync();
        await host.Activity.StartActivityAsync();

        var ex = await Assert.ThrowsAsync<ConflictException>(() => host.Activity.StartNewActivityAsync());
        Assert.Contains("먼저 마감", ex.Message);
    }

    /// <summary>같은 날 두 세션에 모두 출석해도 달력에는 하루 치만 표시된다.</summary>
    [Fact]
    public async Task Calendar_MergesTwoSessionsOnSameDate()
    {
        await using var host = new ServiceHost();
        var member = await host.SeedUserAsync("민수", "202610001");
        var date = Domain.KstClock.Today(host.Time);
        var session1 = await host.SeedActivityAsync(date, ActivityStatus.ActivityClosed);
        var session2 = await host.SeedActivityAsync(date, ActivityStatus.ActivityClosed);

        await using var db = host.Db.CreateContext();
        db.Attendances.AddRange(
            new Attendance { ActivityId = session1.Id, UserId = member, AttendanceType = AttendanceType.Otp, CheckedAt = DateTime.UnixEpoch, CreatedAt = DateTime.UnixEpoch },
            new Attendance { ActivityId = session2.Id, UserId = member, AttendanceType = AttendanceType.Otp, CheckedAt = DateTime.UnixEpoch, CreatedAt = DateTime.UnixEpoch });
        await db.SaveChangesAsync();

        var calendar = await host.Attendance.GetMyCalendarAsync(member, date.Year, date.Month);
        var day = calendar.Days.Single(d => d.Date == date); // 중복 없이 하루 치만
        Assert.True(day.Kind == CalendarDayKind.Attended);
        Assert.Equal(2, calendar.MonthCount); // 세션 단위 출석 수
    }

    /// <summary>같은 날 마감 세션이 여러 개면 최신 세션이 현재 활동으로 결정된다.</summary>
    [Fact]
    public async Task TwoClosedSessions_SameDate_CurrentResolvesLatest()
    {
        await using var host = new ServiceHost();
        var date = Domain.KstClock.Today(host.Time);
        await host.SeedActivityAsync(date, ActivityStatus.ActivityClosed); // 구세션
        host.AsAdmin();
        var session2 = await host.Activity.StartNewActivityAsync();
        await host.Activity.CloseActivityAsync();

        var current = await host.Activity.GetOrCreateTodayAsync();
        Assert.Equal(session2.ActivityId, current.ActivityId); // 최신 세션 우선
    }

    /// <summary>새 세션 시작→마감 후 되살리기는 새 세션을 대상으로 한다.</summary>
    [Fact]
    public async Task ReopenAfterStartNew_TargetsNewSession()
    {
        await using var host = new ServiceHost();
        host.AsAdmin();
        var session1 = await host.Activity.GetOrCreateTodayAsync();
        await host.Activity.StartActivityAsync();
        await host.Activity.CloseActivityAsync();
        var session2 = await host.Activity.StartNewActivityAsync();
        await host.Activity.CloseActivityAsync();

        var reopened = await host.Activity.ReopenActivityAsync();
        Assert.Equal(session2.ActivityId, reopened.ActivityId);
        Assert.Equal(ActivityStatus.AttendanceOpen, reopened.Status);

        var member = await host.SeedUserAsync("민수", "202610003");
        host.AsMember(member);
        await host.Attendance.CheckInWithOtpAsync(session2.OtpCode!); // 되돌린 새 세션 출석 가능

        await using var db = host.Db.CreateContext();
        var s1 = await db.Activities.AsNoTracking().FirstAsync(a => a.Id == session1.ActivityId);
        Assert.Equal(ActivityStatus.ActivityClosed, s1.Status); // 구세션은 종료 유지
    }

    /// <summary>같은 날 마감 세션 2개 미출석이어도 결석 표시는 하루 치만 된다.</summary>
    [Fact]
    public async Task Calendar_MissesSameDateSessionsOnce()
    {
        await using var host = new ServiceHost();
        var member = await host.SeedUserAsync("민수", "202610002");
        var date = Domain.KstClock.Today(host.Time);
        await host.SeedActivityAsync(date, ActivityStatus.ActivityClosed);
        await host.SeedActivityAsync(date, ActivityStatus.ActivityClosed);

        var calendar = await host.Attendance.GetMyCalendarAsync(member, date.Year, date.Month);
        Assert.Equal(1, calendar.Days.Count(d => d.Date == date && d.Kind == CalendarDayKind.Missed));
    }
}
