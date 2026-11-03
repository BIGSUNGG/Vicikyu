using Microsoft.EntityFrameworkCore;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Application.Exceptions;
using VolleyballClub.Domain.Enums;

namespace VolleyballClub.Tests;

/// <summary>관리자 활동 정정 — 출석 멱등성, 마크/활동별 부원 목록, 삭제 재시도, 사유 표기.</summary>
public sealed partial class AdminActivityManagementTests
{
    [Fact]
    public async Task Day_members_are_scoped_to_activity_kind()
    {
        await using var h = new Host(); await h.Seed();
        var rows = await h.List();
        var completed = rows.Single(r => r.Kind == DayKind.Completed);
        var mark = rows.Single(r => r.Date == new DateOnly(2026, 9, 15)); // 없음 마크

        // 완료 활동은 전체 활성 부원을 나열하고, 예정/없음 표시에는 출석이 없다
        Assert.Equal(new[] { "a", "admin", "unmapped" },
            (await h.Service.GetMembersAsync(completed.Key)).Select(m => m.UserId).OrderBy(x => x).ToArray());
        Assert.Empty(await h.Service.GetMembersAsync(mark.Key));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Repeated_attendance_request_is_idempotent_without_duplicate_audit(bool attended)
    {
        await using var h = new Host(); await h.Seed(); var row = await h.Completed();
        await h.Service.SetAttendanceAsync(row.Key, "a", new(attended)); // 첫 요청(false는 이미 0출석 — 상태 변화 없음)
        await h.Service.SetAttendanceAsync(row.Key, "a", new(attended)); // 재시도 — 중복 없음
        await using var db = h.Db.CreateContext();
        Assert.Equal(attended ? 1 : 0, await db.Attendances.CountAsync());
        Assert.Equal(attended ? 1 : 0, await db.AuditLogs.CountAsync(a =>
            a.Action == (attended ? AdminAction.AttendanceAdd : AdminAction.AttendanceRemove)));
    }

    [Fact]
    public async Task Activity_deletion_retry_is_noop_with_single_audit()
    {
        await using var h = new Host(); await h.Seed();
        var app = await h.App(new(2026, 10, 1), ActivityStatus.Ready);
        await h.Service.DeleteAsync($"app:{app.Id}");
        // 앱 키 재시도는 이미 지워졌음을 알린다(마크 키와 달리 NotFound)
        await Assert.ThrowsAsync<NotFoundException>(() => h.Service.DeleteAsync($"app:{app.Id}"));
        await using var db = h.Db.CreateContext();
        Assert.False(await db.Activities.AnyAsync(a => a.Id == app.Id));
        Assert.Single(await db.Activities.ToListAsync()); // Seed의 완료 활동만 남는다
        Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.Action == AdminAction.ActivityCancel));
    }

    [Theory]
    [InlineData("시험(중간)")]
    [InlineData("(시험)")]
    [InlineData("(시험(중간))")]
    public async Task Nested_and_outer_parentheses_reason_round_trip_exactly_with_korean_state(string reason)
    {
        await using var h = new Host(); await h.Seed();
        var row = await h.Service.CreateAsync(new(new(2026, 10, 2), DayKind.NoActivity, Note: reason));
        var imported = (await h.List()).Single(r => r.Key == row.Key);
        Assert.Equal(reason, imported.Note); Assert.Equal($"활동 없음({reason})", imported.StateText);
        Assert.Equal(row, imported);
    }
}
