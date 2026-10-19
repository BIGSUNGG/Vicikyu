using Microsoft.EntityFrameworkCore;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Domain.Entities;
using VolleyballClub.Domain.Enums;
using VolleyballClub.Infrastructure.Services;
using Xunit;

namespace VolleyballClub.Tests;

/// <summary>활동의 부원 귀속은 시트 탭 매핑이 유일한 근거다 — 앱 활동도 예외 없다(JoinedAt 미사용).
/// 어느 학기 탭에도 속하지 않는 날짜(방학·시트 미등록)는 아무 부원에게도 귀속하지 않는다.</summary>
public class MemberAttributionTests
{
    // 고정 시각 2026-10-01 KST → 기본 학기 규칙 9/1~12/31, 탭 '2026-2'(2026-08-01 ~ 2027-01-31)
    private static ServiceHost NewHost() => new(formatResolver: new SheetFormatResolver(SemesterTabFormat.All()));

    /// <summary>부원 + 출석 시트 + 지정 탭 매핑을 함께 시드한다.</summary>
    private static async Task<string> SeedMemberAsync(ServiceHost host, string name, string studentNumber, params string[] tabs)
    {
        var userId = await host.SeedUserAsync(name, studentNumber);
        await using var db = host.Db.CreateContext();
        if (!await db.SheetDefinitions.AnyAsync(s => s.Kind == SheetKind.Attendance))
        {
            db.SheetDefinitions.Add(new SheetDefinition
            {
                Name = "출석부", SpreadsheetId = "ss-attribution", Kind = SheetKind.Attendance,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        var sheetId = await db.SheetDefinitions.Where(s => s.Kind == SheetKind.Attendance).Select(s => s.Id).FirstAsync();
        foreach (var tab in tabs)
            db.SheetMemberMappings.Add(new SheetMemberMapping { SheetDefinitionId = sheetId, TabName = tab, RowLabel = name, UserId = userId });
        await db.SaveChangesAsync();
        return userId;
    }

    [Fact]
    public async Task 앱_활동은_부원이_매핑된_학기만_귀속된다()
    {
        await using var host = NewHost();
        var spring = await SeedMemberAsync(host, "봄부원", "202610001", "2026-1"); // 2026-2 미매핑
        var autumn = await SeedMemberAsync(host, "가을부원", "202610002", "2026-2");

        await host.SeedActivityAsync(new DateOnly(2026, 3, 10), ActivityStatus.ActivityClosed); // 2026-1
        await host.SeedActivityAsync(new DateOnly(2026, 9, 10), ActivityStatus.ActivityClosed); // 2026-2

        // 캘린더: 미매핑 학기 앱 활동은 결석(Missed)으로 표시하지 않는다
        Assert.Equal(1, (await host.Attendance.GetMyCalendarAsync(spring, 2026, 3)).Days.Count(d => d.Kind == CalendarDayKind.Missed));
        Assert.DoesNotContain((await host.Attendance.GetMyCalendarAsync(spring, 2026, 9)).Days, d => d.Kind == CalendarDayKind.Missed);
        Assert.Equal(1, (await host.Attendance.GetMyCalendarAsync(autumn, 2026, 9)).Days.Count(d => d.Kind == CalendarDayKind.Missed));

        // 기록: 미매핑 학기 앱 활동은 목록에 나타나지 않는다
        var springRecords = await host.Attendance.GetMyRecordsAsync(spring, 2026, 9);
        Assert.Empty(springRecords.Records);
        Assert.Equal(0, springRecords.MonthActivities);
        var autumnRecords = await host.Attendance.GetMyRecordsAsync(autumn, 2026, 9);
        Assert.Single(autumnRecords.Records);
        Assert.False(autumnRecords.Records[0].IsAttended);

        // 학기 통계(고정 시각 10월 → 2026-2): 분모도 매핑 학기만 센다
        Assert.Equal(0, (await host.Attendance.GetMySemesterStatsAsync(spring)).SemesterActivities);
        Assert.Equal(1, (await host.Attendance.GetMySemesterStatsAsync(autumn)).SemesterActivities);
    }

    [Fact]
    public async Task 출석_추가_후보도_부원이_매핑된_학기만_나타난다()
    {
        await using var host = NewHost().AsAdmin();
        var spring = await SeedMemberAsync(host, "봄부원", "202610001", "2026-1"); // 현재 학기(2026-2) 미매핑
        var autumn = await SeedMemberAsync(host, "가을부원", "202610002", "2026-2");
        await host.SeedActivityAsync(new DateOnly(2026, 9, 20), ActivityStatus.ActivityClosed); // 현재 학기 앱 활동

        var springRecords = await host.Attendance.GetAdminRecordsAsync(spring);
        Assert.Empty(springRecords.AddableActivities); // 추가가 어차피 행 매핑 검증에서 거부된다

        var autumnRecords = await host.Attendance.GetAdminRecordsAsync(autumn);
        Assert.Equal(new DateOnly(2026, 9, 20), autumnRecords.AddableActivities.Single().Date);
    }

    [Fact]
    public async Task 어느_탭에도_속하지_않는_날짜는_아무_부원에게도_귀속하지_않는다()
    {
        // 전략은 2026-1(2/1~7/31)만 존재 — 9월 활동은 방학·시트 미등록 날짜와 같은 미커버 상태다
        await using var host = new ServiceHost(
            formatResolver: new SheetFormatResolver([new FakeSemesterFormat("2026-1") { From = new(2026, 2, 1), To = new(2026, 7, 31) }]));
        var member = await SeedMemberAsync(host, "박유진", "202610003", "2026-1");
        await host.SeedActivityAsync(new DateOnly(2026, 9, 10), ActivityStatus.ActivityClosed);

        Assert.DoesNotContain((await host.Attendance.GetMyCalendarAsync(member, 2026, 9)).Days, d => d.Kind == CalendarDayKind.Missed);
        var records = await host.Attendance.GetMyRecordsAsync(member, 2026, 9);
        Assert.Empty(records.Records);
        Assert.Equal(0, records.MonthActivities);
        Assert.Equal(0, (await host.Attendance.GetMySemesterStatsAsync(member)).SemesterActivities);
    }
}
