using Microsoft.EntityFrameworkCore;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Application.Exceptions;
using VolleyballClub.Domain.Entities;
using VolleyballClub.Domain.Enums;
using VolleyballClub.Infrastructure.Persistence;
using Xunit;

namespace VolleyballClub.Tests;

/// <summary>학기별 부원 행 관리 — 미분류 추가·중복 라벨·이중 집계 방지·매핑 덮어쓰기 회귀.</summary>
public class SemesterMemberTests
{
    [Fact]
    public async Task 미분류_행_추가가_된다()
    {
        await using var host = new ServiceHost();
        host.AsAdmin();

        var row = await host.SheetRows.AddRowAsync(new SemesterMemberCreateRequest(0, "졸업생", null));

        await using var db = host.Db.CreateContext();
        var stored = await db.SemesterMembers.SingleAsync(m => m.Id == row.Id);
        Assert.Null(stored.SemesterId);
        Assert.Equal("졸업생", stored.Label);
        Assert.Equal("졸업생", stored.Name);
        Assert.Null(stored.MappedUserId);
    }

    [Fact]
    public async Task 같은_학기_같은_라벨_행_추가는_거부된다()
    {
        await using var host = new ServiceHost();
        host.AsAdmin();
        await host.Semester.CreateAsync(new SemesterSaveRequest("2026-2", new(2026, 9, 1), new(2026, 12, 31)));
        var semester = await host.Db.CreateContext().SemesterPeriods.SingleAsync();

        await host.SheetRows.AddRowAsync(new SemesterMemberCreateRequest(semester.Id, "졸업생", null));
        await Assert.ThrowsAsync<ConflictException>(
            () => host.SheetRows.AddRowAsync(new SemesterMemberCreateRequest(semester.Id, "졸업생", null)));
        // 미분류(0)와는 라벨이 같아도 공존한다
        await host.SheetRows.AddRowAsync(new SemesterMemberCreateRequest(0, "졸업생", null));
    }

    [Fact]
    public async Task 다른_학기_행에_이미_있는_활동_출석_추가는_이중_집계를_막기_위해_거부된다()
    {
        await using var host = new ServiceHost();
        host.AsAdmin();
        var member = await host.SeedUserAsync("김민수", "2025");
        var activity = await host.SeedActivityAsync(new DateOnly(2026, 10, 1), ActivityStatus.ActivityClosed);

        // 미분류 행 2개를 같은 계정에 매핑(미분류는 다중 매핑 허용)
        var rowA = await host.SheetRows.AddRowAsync(new SemesterMemberCreateRequest(0, "행A", null));
        var rowB = await host.SheetRows.AddRowAsync(new SemesterMemberCreateRequest(0, "행B", null));
        await host.SheetRows.SetMappingAsync(rowA.Id, member);
        await host.SheetRows.SetMappingAsync(rowB.Id, member);

        await host.SheetRows.SetRowAttendanceAsync(rowA.Id, activity.Id, true);
        // 행B에 같은 활동을 추가하면 같은 계정으로 이중 집계된다 — 거부
        await Assert.ThrowsAsync<ConflictException>(
            () => host.SheetRows.SetRowAttendanceAsync(rowB.Id, activity.Id, true));
        // 행A의 출석을 취소하면 행B에 추가할 수 있다
        await host.SheetRows.SetRowAttendanceAsync(rowA.Id, activity.Id, false);
        await host.SheetRows.SetRowAttendanceAsync(rowB.Id, activity.Id, true);

        await using var db = host.Db.CreateContext();
        Assert.Equal(1, await db.SemesterAttendances.CountAsync(sa => sa.ActivityId == activity.Id));
    }

    [Fact]
    public async Task 같은_학기_매핑은_이전_행_출석을_합집합으로_옮기고_이전_행을_삭제한다()
    {
        await using var host = new ServiceHost();
        host.AsAdmin();
        var member = await host.SeedUserAsync("김회장", "2026");
        await host.Semester.CreateAsync(new SemesterSaveRequest("26년2학기", new(2026, 9, 1), new(2026, 12, 31)));
        var semester = await host.Db.CreateContext().SemesterPeriods.SingleAsync();
        var d1 = await host.SeedActivityAsync(new DateOnly(2026, 10, 1), ActivityStatus.ActivityClosed);
        var d2 = await host.SeedActivityAsync(new DateOnly(2026, 10, 8), ActivityStatus.ActivityClosed);

        var rowA = await host.SheetRows.AddRowAsync(new SemesterMemberCreateRequest(semester.Id, "김회장 A행", null));
        await host.SheetRows.SetMappingAsync(rowA.Id, member);
        await host.SheetRows.SetRowAttendanceAsync(rowA.Id, d1.Id, true);
        var rowB = await host.SheetRows.AddRowAsync(new SemesterMemberCreateRequest(semester.Id, "김회장 B행", null));
        await host.SheetRows.SetRowAttendanceAsync(rowB.Id, d2.Id, true);

        await host.SheetRows.SetMappingAsync(rowB.Id, member); // 덮어쓰기 — B가 A를 대체

        await using var db = host.Db.CreateContext();
        var row = await db.SemesterMembers.Include(m => m.Attendances).SingleAsync(m => m.Id == rowB.Id);
        Assert.Equal(member, row.MappedUserId);
        Assert.Equal(2, row.Attendances.Count);
        Assert.Contains(row.Attendances, a => a.ActivityId == d1.Id); // A행 출석이 이동됨
        Assert.Contains(row.Attendances, a => a.ActivityId == d2.Id);
        Assert.False(await db.SemesterMembers.AnyAsync(m => m.Id == rowA.Id)); // A행 삭제
        Assert.Equal(1, await db.SemesterMembers.CountAsync(m => m.MappedUserId == member && m.SemesterId == semester.Id));
        Assert.Contains(await db.AuditLogs.ToListAsync(),
            a => a.Action == AdminAction.MemberUpdate && a.Details.Contains("매핑 대체"));
    }

    [Fact]
    public async Task 매핑_덮어쓰기에서_같은_활동이_양쪽_행에_있으면_한_건만_남는다()
    {
        await using var host = new ServiceHost();
        host.AsAdmin();
        var member = await host.SeedUserAsync("김회장", "2026");
        await host.Semester.CreateAsync(new SemesterSaveRequest("26년2학기", new(2026, 9, 1), new(2026, 12, 31)));
        var semester = await host.Db.CreateContext().SemesterPeriods.SingleAsync();
        var activity = await host.SeedActivityAsync(new DateOnly(2026, 10, 1), ActivityStatus.ActivityClosed);

        var rowA = await host.SheetRows.AddRowAsync(new SemesterMemberCreateRequest(semester.Id, "김회장 A행", null));
        await host.SheetRows.SetMappingAsync(rowA.Id, member);
        await host.SheetRows.SetRowAttendanceAsync(rowA.Id, activity.Id, true);
        var rowB = await host.SheetRows.AddRowAsync(new SemesterMemberCreateRequest(semester.Id, "김회장 B행", null));
        await host.SheetRows.SetRowAttendanceAsync(rowB.Id, activity.Id, true);

        await host.SheetRows.SetMappingAsync(rowB.Id, member); // 둘 다 출석 → 이중 집계 없이 1건만

        await using var db = host.Db.CreateContext();
        var attendance = await db.SemesterAttendances.SingleAsync(sa => sa.ActivityId == activity.Id);
        Assert.Equal(rowB.Id, attendance.SemesterMemberId);
    }

    [Fact]
    public async Task 매핑_덮어쓰기에서_다른_매핑_행에_있는_활동은_이동하지_않는다()
    {
        await using var host = new ServiceHost();
        host.AsAdmin();
        var member = await host.SeedUserAsync("김회장", "2026");
        await host.Semester.CreateAsync(new SemesterSaveRequest("26년2학기", new(2026, 9, 1), new(2026, 12, 31)));
        var semester = await host.Db.CreateContext().SemesterPeriods.SingleAsync();
        var d1 = await host.SeedActivityAsync(new DateOnly(2026, 10, 1), ActivityStatus.ActivityClosed);
        var d2 = await host.SeedActivityAsync(new DateOnly(2026, 10, 8), ActivityStatus.ActivityClosed);

        // 미분류 매핑 행이 d1 출석을 이미 가진다 — 이 부원의 다른 매핑 행
        var rowU = await host.SheetRows.AddRowAsync(new SemesterMemberCreateRequest(0, "김회장 외부행", null));
        await host.SheetRows.SetMappingAsync(rowU.Id, member);
        await host.SheetRows.SetRowAttendanceAsync(rowU.Id, d1.Id, true);

        var rowA = await host.SheetRows.AddRowAsync(new SemesterMemberCreateRequest(semester.Id, "김회장 A행", null));
        await host.SheetRows.SetMappingAsync(rowA.Id, member);
        // A행 d1은 편집 API가 이중 집계로 막는 상태 — 가져오기로 생긴 경우를 시뮬레이션해 직접 삽입
        await using (var seed = host.Db.CreateContext())
        {
            seed.SemesterAttendances.Add(new SemesterAttendance
            {
                SemesterMemberId = rowA.Id, ActivityId = d1.Id, AttendanceType = AttendanceType.Import,
                CheckedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }
        var rowB = await host.SheetRows.AddRowAsync(new SemesterMemberCreateRequest(semester.Id, "김회장 B행", null));
        await host.SheetRows.SetRowAttendanceAsync(rowB.Id, d2.Id, true);

        await host.SheetRows.SetMappingAsync(rowB.Id, member); // 덮어쓰기 — d1은 미분류 행 것만 유지

        await using var db = host.Db.CreateContext();
        Assert.Equal(1, await db.SemesterAttendances.CountAsync(sa => sa.ActivityId == d1.Id));
        Assert.Equal(rowU.Id, (await db.SemesterAttendances.SingleAsync(sa => sa.ActivityId == d1.Id)).SemesterMemberId);
        var row = await db.SemesterMembers.Include(m => m.Attendances).SingleAsync(m => m.Id == rowB.Id);
        Assert.Single(row.Attendances); // d2만 남는다
        Assert.False(await db.SemesterMembers.AnyAsync(m => m.Id == rowA.Id));
    }

    [Fact]
    public async Task 매핑_해제는_행과_출석을_유지한다()
    {
        await using var host = new ServiceHost();
        host.AsAdmin();
        var member = await host.SeedUserAsync("김민수", "2025");
        await host.Semester.CreateAsync(new SemesterSaveRequest("26년2학기", new(2026, 9, 1), new(2026, 12, 31)));
        var semester = await host.Db.CreateContext().SemesterPeriods.SingleAsync();
        var activity = await host.SeedActivityAsync(new DateOnly(2026, 10, 1), ActivityStatus.ActivityClosed);
        var row = await host.SheetRows.AddRowAsync(new SemesterMemberCreateRequest(semester.Id, "김민수 행", null));
        await host.SheetRows.SetMappingAsync(row.Id, member);
        await host.SheetRows.SetRowAttendanceAsync(row.Id, activity.Id, true);

        await host.SheetRows.SetMappingAsync(row.Id, null);

        await using var db = host.Db.CreateContext();
        var stored = await db.SemesterMembers.Include(m => m.Attendances).SingleAsync(m => m.Id == row.Id);
        Assert.Null(stored.MappedUserId);
        Assert.Single(stored.Attendances);
    }
}
