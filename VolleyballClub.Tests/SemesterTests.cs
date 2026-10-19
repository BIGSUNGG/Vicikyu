using VolleyballClub.Application.Dtos;
using VolleyballClub.Application.Exceptions;
using VolleyballClub.Domain.Entities;
using VolleyballClub.Domain.Enums;
using VolleyballClub.Infrastructure.Services;
using Xunit;

namespace VolleyballClub.Tests;

/// <summary>학기 관리 — CRUD 권한/검증 + 현재 학기 선택 규칙(오늘 포함 → 최근 → 기본 규칙 폴백).</summary>
public class SemesterTests
{
    private static readonly DateOnly FallStart = new(2026, 9, 1);
    private static readonly DateOnly FallEnd = new(2026, 12, 18);

    [Fact]
    public async Task Admin_CreateUpdateDelete_Semester_Works()
    {
        await using var host = new ServiceHost();
        var admin = await host.SeedUserAsync("김회장", "admin");
        host.AsAdmin(admin);

        var created = await host.Semester.CreateAsync(new SemesterSaveRequest("2026년 2학기", FallStart, FallEnd));
        Assert.Equal("2026년 2학기", created.Name);

        var updated = await host.Semester.UpdateAsync(created.Id, new SemesterSaveRequest("2026년 2학기(변경)", FallStart, new DateOnly(2026, 12, 20)));
        Assert.Equal(new DateOnly(2026, 12, 20), updated.EndDate);

        await host.Semester.DeleteAsync(created.Id);
        Assert.Empty(await host.Semester.GetSemestersAsync());
    }

    [Fact]
    public async Task Semester_InvalidRange_Throws()
    {
        await using var host = new ServiceHost();
        var admin = await host.SeedUserAsync("김회장", "admin");
        host.AsAdmin(admin);

        await Assert.ThrowsAsync<RuleViolationException>(() =>
            host.Semester.CreateAsync(new SemesterSaveRequest("역전 학기", FallEnd, FallStart)));
        await Assert.ThrowsAsync<RuleViolationException>(() =>
            host.Semester.CreateAsync(new SemesterSaveRequest("  ", FallStart, FallEnd)));
    }

    [Fact]
    public async Task Member_CreateSemester_ThrowsForbidden()
    {
        await using var host = new ServiceHost();
        var member = await host.SeedUserAsync("김민수", "202512001");
        host.AsMember(member);

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            host.Semester.CreateAsync(new SemesterSaveRequest("2026년 2학기", FallStart, FallEnd)));
    }

    [Fact]
    public async Task GetSemesters_MarksCurrentSemester()
    {
        await using var host = new ServiceHost(); // 고정 시각 2026-10-01 18:00 KST
        var admin = await host.SeedUserAsync("김회장", "admin");
        host.AsAdmin(admin);
        await host.Semester.CreateAsync(new SemesterSaveRequest("2026년 1학기", new DateOnly(2026, 3, 1), new DateOnly(2026, 8, 31)));
        var current = await host.Semester.CreateAsync(new SemesterSaveRequest("2026년 2학기", FallStart, FallEnd));
        await host.Semester.CreateAsync(new SemesterSaveRequest("2027년 1학기", new DateOnly(2027, 3, 1), new DateOnly(2027, 8, 31)));

        var list = await host.Semester.GetSemestersAsync();
        Assert.True(list.Single(s => s.Id == current.Id).IsCurrent);
        Assert.Equal(2, list.Count(s => !s.IsCurrent));
    }

    /// <summary>학기가 전혀 없으면 기존 고정 규칙(10월 → 9/1~12/31)으로 폴백한다.</summary>
    [Fact]
    public async Task NoSemesters_RankingUsesDefaultRange()
    {
        await using var host = new ServiceHost();
        var admin = await host.SeedUserAsync("김회장", "admin");
        await host.SeedUserAsync("김민수", "202512001");
        await SeedAttendanceOnAsync(host.Db, "2026-09-10");
        await SeedAttendanceOnAsync(host.Db, "2026-03-10"); // 기본 규칙상 1학기 → 학기 집계 제외

        host.AsMember(admin);
        var ranking = await host.Attendance.GetRankingAsync(RankingPeriod.Semester);
        Assert.All(ranking, r => Assert.Equal(1, r.Count)); // 9월 활동만 집계
    }

    /// <summary>관리자가 만든 학기 기간이 출석 랭킹 집계 구간을 바꾼다.</summary>
    [Fact]
    public async Task CustomSemester_RankingFollowsConfiguredRange()
    {
        await using var host = new ServiceHost();
        var admin = await host.SeedUserAsync("김회장", "admin");
        await host.SeedUserAsync("김민수", "202512001");
        host.AsAdmin(admin);
        await host.Semester.CreateAsync(new SemesterSaveRequest("후반기", new DateOnly(2026, 10, 15), new DateOnly(2026, 12, 31)));

        await SeedAttendanceOnAsync(host.Db, "2026-10-01"); // 학기 밖 — 제외
        await SeedAttendanceOnAsync(host.Db, "2026-10-20"); // 학기 안 — 포함

        var ranking = await host.Attendance.GetRankingAsync(RankingPeriod.Semester);
        Assert.All(ranking, r => Assert.Equal(1, r.Count));
    }

    /// <summary>관리자가 만든 학기 이름·기간이 부원 학기 통계/캘린더 DTO에 노출된다.</summary>
    [Fact]
    public async Task CustomSemester_StatsDtoCarriesNameAndRange()
    {
        await using var host = new ServiceHost();
        var admin = await host.SeedUserAsync("김회장", "admin");
        var member = await host.SeedUserAsync("김민수", "202512001");
        host.AsAdmin(admin);
        await host.Semester.CreateAsync(new SemesterSaveRequest("2026 후기", new DateOnly(2026, 9, 1), new DateOnly(2026, 12, 18)));

        var stats = await host.Attendance.GetMySemesterStatsAsync(member);
        Assert.Equal("2026 후기", stats.Semester?.Name);
        Assert.Equal(new DateOnly(2026, 9, 1), stats.Semester?.Start);
        Assert.Equal(new DateOnly(2026, 12, 18), stats.Semester?.End);

        var calendar = await host.Attendance.GetMyCalendarAsync(member, 2026, 10);
        Assert.Equal("2026 후기", calendar.Semester?.Name);
    }

    /// <summary>학기가 없으면 이름 없는 기본 규칙 범위로 집계된다(Name=null → 화면은 '이번 학기').</summary>
    [Fact]
    public async Task NoSemesters_StatsDtoHasDefaultScopeWithoutName()
    {
        await using var host = new ServiceHost();
        var member = await host.SeedUserAsync("김민수", "202512001");

        var stats = await host.Attendance.GetMySemesterStatsAsync(member);
        Assert.Null(stats.Semester?.Name);
        Assert.Equal(new DateOnly(2026, 9, 1), stats.Semester?.Start); // 고정 시각 10월 → 기본 규칙 2학기
    }

    /// <summary>개최된 활동 중 미출석일만 Missed(CalendarDayKind)로 노출 — 오늘 출석 중 활동은 결석 아님.</summary>
    [Fact]
    public async Task Calendar_MarksMissedHeldActivitiesOnly()
    {
        await using var host = new ServiceHost(formatResolver: new SheetFormatResolver(SemesterTabFormat.All())); // 고정 시각 2026-10-01 KST
        var member = await host.SeedUserAsync("김민수", "202512001");
        await host.SeedManagedSheetAsync(member); // 2026-2 탭 매핑 — 매핑 학기 활동만 귀속된다

        await SeedHeldActivityAsync(host.Db, "2026-09-25"); // 마감 활동, 미출석 → 표시
        await SeedHeldActivityAsync(host.Db, "2026-09-26", member); // 마감 활동, 출석 → 초록
        await SeedOpenActivityAsync(host.Db, "2026-10-01"); // 오늘 출석 중, 미출석 → 표시 안 함

        var september = await host.Attendance.GetMyCalendarAsync(member, 2026, 9);
        Assert.True(september.Days.Single(d => d.Date == new DateOnly(2026, 9, 25)).Kind == CalendarDayKind.Missed);
        var attended = september.Days.Single(d => d.Date == new DateOnly(2026, 9, 26));
        Assert.True(attended.Kind == CalendarDayKind.Attended);
        Assert.True(attended.Kind != CalendarDayKind.Missed);

        var october = await host.Attendance.GetMyCalendarAsync(member, 2026, 10);
        Assert.DoesNotContain(october.Days, d => d.Kind == CalendarDayKind.Missed);
    }

    /// <summary>시트의 예정/없음 날징은 캘린더에 상태로 표시되고, 실제 출석·미출석 표시가 우선한다.</summary>
    [Fact]
    public async Task Calendar_ShowsScheduledAndNoActivityDays()
    {
        await using var host = new ServiceHost(formatResolver: new SheetFormatResolver(SemesterTabFormat.All())); // 고정 시각 2026-10-01 KST
        var member = await host.SeedUserAsync("김민수", "202512001");
        await host.SeedManagedSheetAsync(member); // 2026-2 탭 매핑 — 매핑 학기 활동만 귀속된다

        await SeedHeldActivityAsync(host.Db, "2026-10-16"); // 마감 활동, 미출석 — 마크보다 우선
        await using (var ctx = host.Db.CreateContext())
        {
            ctx.SheetDayMarks.AddRange(
                new SheetDayMark { Date = new DateOnly(2026, 10, 9), Kind = SheetDayKind.NoActivity },
                new SheetDayMark { Date = new DateOnly(2026, 10, 16), Kind = SheetDayKind.Scheduled }, // 스테일 마크
                new SheetDayMark { Date = new DateOnly(2026, 10, 30), Kind = SheetDayKind.Scheduled });
            await ctx.SaveChangesAsync();
        }

        var october = await host.Attendance.GetMyCalendarAsync(member, 2026, 10);

        var byDate = october.Days.ToDictionary(d => d.Date);
        Assert.Equal(CalendarDayKind.Missed, byDate[new DateOnly(2026, 10, 16)].Kind); // 활동이 마크에 우선
        Assert.Equal(CalendarDayKind.NoActivity, byDate[new DateOnly(2026, 10, 9)].Kind);
        Assert.Equal(CalendarDayKind.Scheduled, byDate[new DateOnly(2026, 10, 30)].Kind);
        Assert.DoesNotContain(october.Days, d => d.Date == new DateOnly(2026, 10, 15)); // 마크 없는 날은 표시 없음
    }

    /// <summary>마감된 활동 행 심기 — userId가 있으면 출석까지.</summary>
    private static async Task SeedHeldActivityAsync(TestDb db, string date, string? userId = null)
    {
        await using var ctx = db.CreateContext();
        var activity = new Activity
        {
            ActivityDate = DateOnly.Parse(date),
            Status = ActivityStatus.ActivityClosed,
            CreatedByUserId = "seed",
            CreatedAt = DateTime.UnixEpoch,
            UpdatedAt = DateTime.UnixEpoch,
        };
        ctx.Activities.Add(activity);
        await ctx.SaveChangesAsync();
        if (userId is not null)
        {
            ctx.Attendances.Add(new Attendance
            {
                ActivityId = activity.Id,
                UserId = userId,
                AttendanceType = AttendanceType.Admin,
                CheckedAt = DateTime.UnixEpoch,
                CreatedAt = DateTime.UnixEpoch,
            });
            await ctx.SaveChangesAsync();
        }
    }

    /// <summary>오늘 출석 중(AttendanceOpen) 활동 행 심기.</summary>
    private static async Task SeedOpenActivityAsync(TestDb db, string date)
    {
        await using var ctx = db.CreateContext();
        ctx.Activities.Add(new Activity
        {
            ActivityDate = DateOnly.Parse(date),
            Status = ActivityStatus.AttendanceOpen,
            OtpCode = "1234",
            AttendanceOpenedAt = DateTime.UnixEpoch,
            CreatedByUserId = "seed",
            CreatedAt = DateTime.UnixEpoch,
            UpdatedAt = DateTime.UnixEpoch,
        });
        await ctx.SaveChangesAsync();
    }

    /// <summary>직접 활동+출석 행을 심는다(랭킹은 status != Ready 활동만 집계).</summary>
    private static async Task SeedAttendanceOnAsync(TestDb db, string date)
    {
        await using var ctx = db.CreateContext();
        var activity = new Activity
        {
            ActivityDate = DateOnly.Parse(date),
            Status = ActivityStatus.ActivityClosed,
            CreatedByUserId = "seed",
            CreatedAt = DateTime.UnixEpoch,
            UpdatedAt = DateTime.UnixEpoch,
        };
        ctx.Activities.Add(activity);
        await ctx.SaveChangesAsync();
        ctx.Attendances.Add(new Attendance
        {
            ActivityId = activity.Id,
            UserId = ctx.Users.First().Id,
            AttendanceType = AttendanceType.Admin,
            CheckedAt = DateTime.UnixEpoch,
            CreatedAt = DateTime.UnixEpoch,
        });
        await ctx.SaveChangesAsync();
    }
}
