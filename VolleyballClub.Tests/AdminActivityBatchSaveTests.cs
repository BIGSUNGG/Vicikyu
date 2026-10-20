using Microsoft.EntityFrameworkCore;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Application.Exceptions;
using VolleyballClub.Domain.Entities;
using VolleyballClub.Domain.Enums;
using VolleyballClub.Infrastructure.Services;

namespace VolleyballClub.Tests;

/// <summary>변경 저장 1회 = 활동 편집 + 출석 일괄 적용(EditAsync Attendances) 검증.</summary>
public sealed partial class AdminActivityManagementTests
{
    [Fact]
    public async Task Batch_save_completes_activity_and_sets_attendance_in_one_edit()
    {
        await using var h = new Host(); await h.Seed();
        var row = (await h.List()).Single(a => a.Kind == SheetDayKind.Scheduled);
        var edited = await h.Service.EditAsync(row.Key, new(row.Date, SheetDayKind.Completed,
            Attendances: [new ActivityAttendanceSetRequest("a", true)]));
        Assert.Equal(SheetDayKind.Completed, edited.Kind);
        Assert.Equal(1, edited.AttendanceCount);
        var members = await h.Service.GetMembersAsync(edited.Key);
        Assert.True(members.Single(m => m.UserId == "a").IsAttended);
        // 시트 푸시까지 한 번에 반영 — 원본 행 A에 출석 표시가 남는다.
        Assert.Contains("출석", h.Sheets.Documents["first"][0].Rows.Single(r => r.FirstOrDefault() == "A"));
    }

    [Fact]
    public async Task Batch_save_rejects_attendance_for_no_activity_before_any_commit()
    {
        await using var h = new Host(); await h.Seed();
        var row = (await h.List()).Single(a => a.Kind == SheetDayKind.Scheduled);
        await Assert.ThrowsAsync<ConflictException>(() => h.Service.EditAsync(row.Key, new(row.Date, SheetDayKind.NoActivity,
            Attendances: [new ActivityAttendanceSetRequest("a", true)])));
        // 사전 검증이므로 활동·시트는 부분 적용 없이 원 상태 유지
        var unchanged = (await h.List()).Single(a => a.Key == row.Key);
        Assert.Equal(SheetDayKind.Scheduled, unchanged.Kind);
        await using var db = h.Db.CreateContext();
        Assert.Empty(await db.Attendances.ToListAsync());
    }

    [Fact]
    public async Task Batch_save_rejects_scheduled_revert_with_pending_attendance_before_any_commit()
    {
        await using var h = new Host(); await h.Seed();
        var row = await h.Completed();
        // 0출석 완료 활동을 예정으로 되돌리며 출석을 함께 저장하면 커밋 전에 거부된다(부분 적용 없음).
        await Assert.ThrowsAsync<ConflictException>(() => h.Service.EditAsync(row.Key, new(row.Date, SheetDayKind.Scheduled,
            Attendances: [new ActivityAttendanceSetRequest("a", true)])));
        var unchanged = (await h.List()).Single(a => a.Key == row.Key);
        Assert.Equal(SheetDayKind.Completed, unchanged.Kind);
        await using var db = h.Db.CreateContext();
        Assert.Empty(await db.Attendances.ToListAsync());
    }

    [Fact]
    public async Task Batch_save_removes_attendance_and_team_without_extra_confirmation()
    {
        await using var h = new Host(); await h.Seed(); var row = await h.Completed();
        await h.Service.EditAsync(row.Key, new(row.Date, Attendances: [new ActivityAttendanceSetRequest("a", true)]));
        await using (var db = h.Db.CreateContext())
        {
            var team = new Team { ActivityId = row.ActivityId!.Value, Name = "A" }; team.Members.Add(new TeamMember { UserId = "a" });
            db.Teams.Add(team); await db.SaveChangesAsync();
        }
        var edited = await h.Service.EditAsync(row.Key, new(row.Date,
            Attendances: [new ActivityAttendanceSetRequest("a", false)]));
        Assert.Equal(0, edited.AttendanceCount);
        await using var verify = h.Db.CreateContext();
        Assert.Empty(await verify.Attendances.ToListAsync());
        Assert.Empty(await verify.TeamMembers.ToListAsync());
    }
}
