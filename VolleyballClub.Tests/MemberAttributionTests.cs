using Microsoft.EntityFrameworkCore;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Domain.Entities;
using VolleyballClub.Domain.Enums;

namespace VolleyballClub.Tests;

/// <summary>활동의 부원 귀속은 MemberSemesters가 유일한 근거다 — 그 학기에 출석 1건 이상이거나
/// 가입일(JoinedAt, KST)이 학기 안이면 '그 학기에 부원이었다'로 본다.
/// 어느 학기에도 속하지 않는 날짜(방학 등)는 아무 부원에게도 귀속하지 않는다.</summary>
public class MemberAttributionTests
{
    // 고정 시각 2026-10-01 KST → 현재 학기는 2026-2(9/1~12/31)
    private static async Task<ServiceHost> NewHostAsync()
    {
        var host = new ServiceHost();
        host.AsAdmin();
        await host.Semester.CreateAsync(new SemesterSaveRequest("2026-1", new(2026, 3, 1), new(2026, 8, 31)));
        await host.Semester.CreateAsync(new SemesterSaveRequest("2026-2", new(2026, 9, 1), new(2026, 12, 31)));
        return host;
    }

    private static async Task AddAttendanceAsync(ServiceHost host, int activityId, string userId)
    {
        await using var db = host.Db.CreateContext();
        db.Attendances.Add(new Attendance
        {
            ActivityId = activityId,
            UserId = userId,
            AttendanceType = AttendanceType.Admin,
            CheckedAt = DateTime.UnixEpoch,
            CreatedAt = DateTime.UnixEpoch,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task 귀속된_학기의_활동만_결석으로_표시한다()
    {
        await using var host = await NewHostAsync();
        var spring = await host.SeedUserAsync("봄부원", "202610001"); // 가입일 3/2 → 2026-1만 귀속
        var autumn = await host.SeedUserAsync("가을부원", "202610002"); // 2026-2 출석 1건으로 귀속 추가

        await host.SeedActivityAsync(new DateOnly(2026, 3, 10), ActivityStatus.ActivityClosed); // 2026-1
        var sep5 = await host.SeedActivityAsync(new DateOnly(2026, 9, 5), ActivityStatus.ActivityClosed); // 2026-2
        await host.SeedActivityAsync(new DateOnly(2026, 9, 10), ActivityStatus.ActivityClosed); // 2026-2
        await AddAttendanceAsync(host, sep5.Id, autumn);

        // 캘린더: 미귀속 학기 활동은 결석(Missed)으로 표시하지 않는다
        Assert.Equal(1, (await host.Attendance.GetMyCalendarAsync(spring, 2026, 3)).Days.Count(d => d.Kind == CalendarDayKind.Missed));
        Assert.DoesNotContain((await host.Attendance.GetMyCalendarAsync(spring, 2026, 9)).Days, d => d.Kind == CalendarDayKind.Missed);
        Assert.Equal(1, (await host.Attendance.GetMyCalendarAsync(autumn, 2026, 9)).Days.Count(d => d.Kind == CalendarDayKind.Missed));

        // 기록: 미귀속 학기 활동은 목록에 나타나지 않는다
        var springRecords = await host.Attendance.GetMyRecordsAsync(spring, 2026, 9);
        Assert.Empty(springRecords.Records);
        Assert.Equal(0, springRecords.MonthActivities);
        var autumnRecords = await host.Attendance.GetMyRecordsAsync(autumn, 2026, 9);
        Assert.Equal(2, autumnRecords.Records.Count); // 9/5 출석 + 9/10 미출석
        Assert.False(autumnRecords.Records.Single(r => r.Date == new DateOnly(2026, 9, 10)).IsAttended);

        // 학기 통계(고정 시각 10월 → 2026-2): 분모도 귀속 학기만 센다
        Assert.Equal(0, (await host.Attendance.GetMySemesterStatsAsync(spring)).SemesterActivities);
        Assert.Equal(2, (await host.Attendance.GetMySemesterStatsAsync(autumn)).SemesterActivities);
    }

    [Fact]
    public async Task 출석_추가_후보도_귀속된_학기만_나타난다()
    {
        await using var host = await NewHostAsync(); host.AsAdmin();
        var spring = await host.SeedUserAsync("봄부원", "202610001"); // 현재 학기(2026-2) 미귀속
        var autumn = await host.SeedUserAsync("가을부원", "202610002");

        var activity = await host.SeedActivityAsync(new DateOnly(2026, 9, 20), ActivityStatus.ActivityClosed);
        await AddAttendanceAsync(host, activity.Id, autumn); // 2026-2 귀속 확보

        var springRecords = await host.Attendance.GetAdminRecordsAsync(spring);
        Assert.Empty(springRecords.AddableActivities);

        var autumnRecords = await host.Attendance.GetAdminRecordsAsync(autumn);
        Assert.Equal(new DateOnly(2026, 9, 20), autumnRecords.AddableActivities.Single().Date);
    }

    [Fact]
    public async Task 가입일이_학기_안이면_출석_없이도_귀속된다()
    {
        await using var host = await NewHostAsync();
        var member = await host.SeedUserAsync("신입부원", "202610003");
        await using (var db = host.Db.CreateContext())
        {
            var user = await db.Users.SingleAsync(u => u.Id == member);
            user.JoinedAt = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc); // 2026-2 안에 가입
            await db.SaveChangesAsync();
        }
        await host.SeedActivityAsync(new DateOnly(2026, 9, 25), ActivityStatus.ActivityClosed);

        Assert.Equal(1, (await host.Attendance.GetMyCalendarAsync(member, 2026, 9)).Days.Count(d => d.Kind == CalendarDayKind.Missed));
        var records = await host.Attendance.GetMyRecordsAsync(member, 2026, 9);
        Assert.Single(records.Records);
        Assert.Equal(1, (await host.Attendance.GetMySemesterStatsAsync(member)).SemesterActivities);
    }

    [Fact]
    public async Task 어느_학기에도_속하지_않는_날짜는_아무_부원에게도_귀속하지_않는다()
    {
        // 2026-1(3/1~8/31)·2026-2(9/1~12/31)만 존재 — 2월 활동은 방학·미등록 날짜와 같은 미커버 상태다
        await using var host = await NewHostAsync();
        var member = await host.SeedUserAsync("박유진", "202610003"); // 2026-1 귀속(가입일)
        await host.SeedActivityAsync(new DateOnly(2026, 2, 15), ActivityStatus.ActivityClosed);

        Assert.Empty((await host.Attendance.GetMyCalendarAsync(member, 2026, 2)).Days);
        var records = await host.Attendance.GetMyRecordsAsync(member, 2026, 2);
        Assert.Empty(records.Records);
        Assert.Equal(0, records.MonthActivities);
    }
}
