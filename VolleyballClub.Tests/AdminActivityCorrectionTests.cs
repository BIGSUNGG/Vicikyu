using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Application.Exceptions;
using VolleyballClub.Domain.Entities;
using VolleyballClub.Domain.Enums;

namespace VolleyballClub.Tests;

public sealed partial class AdminActivityManagementTests
{
    [Fact]
    public async Task Legacy_blank_dates_stay_visible_without_completed_denominator_and_explicit_empty_completion_round_trips()
    {
        await using var h = new Host(); await h.Seed(tab: "2026-1", sync: false);
        h.Sheets.SetTab("first", new("2026-1", [["부원", "26. 03. 01. 일", "26. 03. 02. 월 활동 완료"], ["A", "", ""]]));
        await h.Sync.SyncAsync();
        var rows = await h.List();
        var blank = rows.Single(r => r.Date.Day == 1); var completed = rows.Single(r => r.Date.Day == 2);
        Assert.Equal(SheetDayKind.Scheduled, blank.Kind); Assert.Null(blank.ActivityId); Assert.False(blank.CanEditAttendance);
        Assert.Equal(SheetDayKind.Completed, completed.Kind); Assert.NotNull(completed.ActivityId); Assert.Equal(0, completed.AttendanceCount);
        await using (var db = h.Db.CreateContext()) Assert.Single(await db.Activities.ToListAsync());
        await h.Service.EditAsync(blank.Key, new(blank.Date, SheetDayKind.Completed));
        Assert.Contains("활동 완료", h.Sheets.Documents["first"][0].Rows[0][1]);
        Assert.All(await h.List(), r => { Assert.Equal(SheetDayKind.Completed, r.Kind); Assert.Equal(0, r.AttendanceCount); });
        await using var verify = h.Db.CreateContext(); Assert.Equal(2, await verify.Activities.CountAsync());
    }

    [Fact]
    public async Task Legacy_toggle_and_export_write_numeric_one_and_explicit_empty_completed_header()
    {
        await using var h = new Host(); await h.Seed(tab: "2026-1");
        h.Sheets.SetTab("first", new("2026-1", [["부원", "26. 03. 01. 일 활동 완료"], ["A", ""], ["총 참여자 수", "=SUM(B2:B2)"]]));
        var row = await h.Completed(); await h.Service.SetAttendanceAsync(row.Key, "a", new(true));
        var tab = await h.Sheets.GetManagedTabAsync("first", "2026-1");
        using (var value = JsonDocument.Parse(tab.RawValues[1][1]))
        { Assert.Equal(1, value.RootElement.GetProperty("numberValue").GetInt32()); Assert.False(value.RootElement.TryGetProperty("stringValue", out _)); }
        var app = await h.App(new(2026, 3, 2));
        await using (var db = h.Db.CreateContext())
        {
            var a = await db.Activities.SingleAsync(a => a.Id == app.Id); a.SheetExportedAt = null;
            db.Attendances.Add(new Attendance { ActivityId = app.Id, UserId = "a", AttendanceType = AttendanceType.Otp }); await db.SaveChangesAsync();
        }
        Assert.True(await h.Sync.ExportClosedActivityAsync(app.Id));
        tab = await h.Sheets.GetManagedTabAsync("first", "2026-1");
        var index = tab.Rows[0].ToList().FindIndex(c => c.Contains("26. 03. 02."));
        using (var value = JsonDocument.Parse(tab.RawValues[1][index])) Assert.Equal(1, value.RootElement.GetProperty("numberValue").GetInt32());
        Assert.Contains("활동 완료", tab.Rows[0][index]);
        await h.Service.SetAttendanceAsync($"app:{app.Id}", "a", new(false));
        var empty = (await h.List()).Single(r => r.ActivityId == app.Id);
        Assert.Equal(SheetDayKind.Completed, empty.Kind); Assert.Equal(0, empty.AttendanceCount);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public async Task Failed_close_export_retries_before_import_and_never_destroys_pending_app(int attendanceCount, bool stillFailing)
    {
        await using var h = new Host(); await h.Seed(); await h.List();
        var app = await h.App(new(2026, 10, 1), ActivityStatus.AttendanceOpen);
        await h.Service.EditAsync($"app:{app.Id}", new(app.ActivityDate));
        await using (var db = h.Db.CreateContext())
        {
            var saved = await db.Activities.SingleAsync(a => a.Id == app.Id);
            saved.Status = ActivityStatus.ActivityClosed; saved.SheetExportedAt = null;
            if (attendanceCount > 0) db.Attendances.Add(new Attendance { ActivityId = app.Id, UserId = "a", AttendanceType = AttendanceType.Otp });
            db.Guests.Add(new Guest { ActivityId = app.Id, Name = "keep guest" });
            var team = new Team { ActivityId = app.Id, Name = "keep team" }; team.Members.Add(new TeamMember { UserId = "a" }); db.Teams.Add(team);
            await db.SaveChangesAsync();
        }
        h.Sheets.BeforeManagedApply = _ => throw new IOException("close export unavailable");
        Assert.False(await h.Sync.ExportClosedActivityAsync(app.Id));
        if (!stillFailing) h.Sheets.BeforeManagedApply = null;
        var report = await h.Sync.SyncAsync();
        if (stillFailing) Assert.Contains(report.Warnings, w => w.Contains("내보내기"));
        else { Assert.Empty(report.Warnings); Assert.Equal(1, report.ExportedColumns); }
        var row = (await h.List()).Single(r => r.ActivityId == app.Id); Assert.Equal(attendanceCount, row.AttendanceCount);
        await using var verify = h.Db.CreateContext();
        var activity = await verify.Activities.SingleAsync(a => a.Id == app.Id);
        Assert.Equal(ActivityStatus.ActivityClosed, activity.Status); Assert.Equal("1234", activity.OtpCode);
        Assert.Equal(stillFailing, activity.SheetExportedAt is null);
        Assert.Equal(attendanceCount, await verify.Attendances.CountAsync(a => a.ActivityId == app.Id));
        Assert.Single(await verify.Guests.Where(g => g.ActivityId == app.Id).ToListAsync());
        Assert.Single(await verify.Teams.Where(t => t.ActivityId == app.Id).ToListAsync());
        Assert.Single(await verify.TeamMembers.Where(m => m.Team!.ActivityId == app.Id).ToListAsync());
        Assert.Empty(await verify.SheetEditJournals.ToListAsync());
    }

    [Fact]
    public async Task Member_rows_and_day_members_are_scoped_to_mapped_semester_tab()
    {
        await using var h = new Host(); await h.Seed();
        // 같은 시트에 2026-1 탭 완료 열 추가 — 'a'는 2026-2 탭에만 매핑되어 있다
        h.Sheets.Documents["first"].Add(new SheetTab("2026-1",
            [["활동 상태", "활동 완료"], ["부원", "26. 03. 02. 월"], ["A", ""]]));
        await h.Sync.SyncAsync();
        var rows = await h.List();
        var spring = rows.Single(r => r.Date == new DateOnly(2026, 3, 2)); // 2026-1 완료 — 'a' 미매핑 학기
        var autumn = rows.Single(r => r.Date == new DateOnly(2026, 9, 1) && r.Kind == SheetDayKind.Completed); // 2026-2 완료

        // 부원 상세 활동 목록: 미매핑 학기 활동 행은 나타나지 않는다
        var details = await h.Service.GetMemberActivitiesAsync("a");
        Assert.DoesNotContain(details.Items, r => r.Date == new DateOnly(2026, 3, 2));
        Assert.Contains(details.Items, r => r.Date == new DateOnly(2026, 9, 1));

        // 일별 부원 목록: 그 활동의 학기 탭에 매핑된 부원만 나열된다
        Assert.Empty(await h.Service.GetMembersAsync(spring.Key));
        Assert.Equal("a", (await h.Service.GetMembersAsync(autumn.Key)).Single().UserId);
    }

    [Fact]
    public async Task One_failed_pending_export_does_not_detach_the_next_successful_export()
    {
        await using var h = new Host(); await h.Seed(); await h.List();
        var first = await h.App(new(2026, 10, 1), ActivityStatus.AttendanceOpen);
        var second = await h.App(new(2026, 10, 2), ActivityStatus.AttendanceOpen);
        foreach (var app in new[] { first, second }) await h.Service.EditAsync($"app:{app.Id}", new(app.ActivityDate));
        string failedSource;
        await using (var db = h.Db.CreateContext())
        {
            foreach (var a in await db.Activities.Where(a => a.Id == first.Id || a.Id == second.Id).ToListAsync())
            { a.Status = ActivityStatus.ActivityClosed; a.SheetExportedAt = null; }
            failedSource = (await db.ActivitySources.SingleAsync(s => s.ActivityId == first.Id)).Id; await db.SaveChangesAsync();
        }
        h.Sheets.BeforeManagedApply = m => { if (m.SourceKey == failedSource) throw new IOException("first export failed"); };
        var report = await h.Sync.SyncAsync(); Assert.Equal(1, report.ExportedColumns); Assert.NotEmpty(report.Warnings);
        await using var verify = h.Db.CreateContext();
        Assert.Null((await verify.Activities.SingleAsync(a => a.Id == first.Id)).SheetExportedAt);
        Assert.NotNull((await verify.Activities.SingleAsync(a => a.Id == second.Id)).SheetExportedAt);
        Assert.Empty(await verify.SheetEditJournals.ToListAsync());
    }

    [Fact]
    public async Task One_member_toggle_preserves_another_mapped_members_newer_manual_mark_and_resync_imports_it()
    {
        await using var h = new Host(); await h.Seed(sync: false);
        var rows = h.Sheets.Documents["first"][0].Rows.Select(r => r.ToList()).ToList();
        rows.Insert(3, ["B", "", "", ""]); h.Sheets.SetTab("first", new("2026-2", rows));
        await using (var db = h.Db.CreateContext())
        { db.SheetMemberMappings.Add(new SheetMemberMapping { SheetDefinitionId = h.SheetId, TabName = "2026-2", UserId = "admin", RowLabel = "B" }); await db.SaveChangesAsync(); }
        await h.Sync.SyncAsync();
        var row = await h.Completed();
        rows = h.Sheets.Documents["first"][0].Rows.Select(r => r.ToList()).ToList();
        while (rows[3].Count < 2) rows[3].Add("");
        rows[3][1] = "출석";
        h.Sheets.SetTab("first", new("2026-2", rows));
        var before = await h.Sheets.GetManagedTabAsync("first", "2026-2");
        await h.Service.SetAttendanceAsync(row.Key, "a", new(true));
        var after = await h.Sheets.GetManagedTabAsync("first", "2026-2");
        Assert.Equal(before.RawValues[3][1], after.RawValues[3][1]); Assert.Equal("출석", after.Rows[3][1]);
        for (var r = 0; r < before.RawValues.Count; r++)
            for (var c = 0; c < before.RawValues[r].Count; c++)
                if (r != 2 || c != 1) Assert.Equal(before.RawValues[r][c], after.RawValues[r][c]);
        await h.Sync.ResyncAsync();
        await using var verify = h.Db.CreateContext();
        Assert.Equal(new[] { "a", "admin" }, await verify.Attendances.Where(a => a.ActivityId == row.ActivityId).OrderBy(a => a.UserId).Select(a => a.UserId).ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Repeated_attendance_request_corrects_remote_even_when_database_already_matches_without_duplicate_audit(bool attended)
    {
        await using var h = new Host(); await h.Seed(); var row = await h.Completed();
        if (attended) await h.Service.SetAttendanceAsync(row.Key, "a", new(true));
        var rows = h.Sheets.Documents["first"][0].Rows.Select(r => r.ToList()).ToList();
        while (rows[2].Count < 2) rows[2].Add("");
        rows[2][1] = attended ? "미출석" : "출석";
        h.Sheets.SetTab("first", new("2026-2", rows));
        await h.Service.SetAttendanceAsync(row.Key, "a", new(attended));
        Assert.Equal(attended ? "출석" : "미출석", h.Sheets.Documents["first"][0].Rows[2][1]);
        var writes = h.Sheets.ManagedWrites.Count;
        await h.Service.SetAttendanceAsync(row.Key, "a", new(attended)); Assert.Equal(writes, h.Sheets.ManagedWrites.Count);
        await h.List();
        await using var db = h.Db.CreateContext(); Assert.Equal(attended ? 1 : 0, await db.Attendances.CountAsync());
        Assert.Equal(attended ? 1 : 0, await db.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task Upgrade_binds_unique_exported_app_history_preserving_original_otp_attendance_and_unmapped_member()
    {
        await using var h = new Host(); await h.Seed(sync: false);
        var rows = h.Sheets.Documents["first"][0].Rows.Select(r => r.ToList()).ToList(); rows[2][1] = "출석"; h.Sheets.SetTab("first", new("2026-2", rows));
        var app = await h.App(new(2026, 9, 1)); var originalTime = new DateTime(2026, 9, 1, 9, 1, 2, DateTimeKind.Utc);
        await using (var db = h.Db.CreateContext())
        {
            db.Attendances.AddRange(new Attendance { ActivityId = app.Id, UserId = "a", AttendanceType = AttendanceType.Otp, CheckedAt = originalTime, CreatedAt = originalTime },
                new Attendance { ActivityId = app.Id, UserId = "unmapped", AttendanceType = AttendanceType.Admin, CheckedAt = originalTime });
            await db.SaveChangesAsync();
        }
        await h.Sync.SyncAsync();
        var row = (await h.List()).Single(r => r.Kind == SheetDayKind.Completed);
        Assert.Equal(app.Id, row.ActivityId); Assert.Equal($"app:{app.Id}", row.Key); Assert.Equal(2, row.AttendanceCount);
        var member = await h.Service.GetMemberActivitiesAsync("a");
        Assert.Equal(1, member.TotalCount); Assert.Equal(1, member.SemesterCount);
        await h.List();
        await using var verify = h.Db.CreateContext(); Assert.Single(await verify.Activities.ToListAsync());
        var a = await verify.Attendances.SingleAsync(a => a.UserId == "a"); Assert.Equal(AttendanceType.Otp, a.AttendanceType); Assert.Equal(originalTime, a.CheckedAt);
        Assert.Equal(2, await verify.Attendances.CountAsync()); Assert.Equal(app.Id, (await verify.ActivitySources.SingleAsync(s => s.Kind == SheetDayKind.Completed)).ActivityId);
    }

    [Fact]
    public async Task Upgrade_partial_import_retry_reconnects_remaining_tab_without_duplicate_history()
    {
        await using var h = new Host(); await h.Seed(tab: "2026-1", sync: false);
        h.Sheets.SetTab("first", new("2026-1", [["부원", "26. 03. 01. 활동 완료"], ["A", ""]]));
        h.Sheets.Documents["first"].Add(new("2026-2", [["활동 상태", "활동 완료"], ["부원", "26. 09. 01."], ["A", ""]]));
        var spring = await h.App(new(2026, 3, 1)); var autumn = await h.App(new(2026, 9, 1));
        var bindings = 0;
        h.Sheets.BeforeManagedApply = mutation =>
        { if (mutation.Kind == SheetMutationKind.Bind && ++bindings == 2) throw new IOException("second tab unavailable"); };
        await Assert.ThrowsAsync<IOException>(() => h.Sync.SyncAsync());
        await using (var db = h.Db.CreateContext())
        { Assert.Equal(spring.Id, (await db.ActivitySources.SingleAsync()).ActivityId); Assert.Equal(2, await db.Activities.CountAsync()); }
        h.Sheets.BeforeManagedApply = null;
        await h.Sync.SyncAsync(); // 부분 실패 재시도 — 프로덕션의 '다시 가져오기'에 해당한다
        var rows = await h.List(); Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.ActivityId == spring.Id); Assert.Contains(rows, r => r.ActivityId == autumn.Id);
        await using var verify = h.Db.CreateContext(); Assert.Equal(2, await verify.Activities.CountAsync());
        Assert.Equal(2, await verify.ActivitySources.CountAsync());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Upgrade_ambiguous_app_or_source_history_conflicts_instead_of_duplicate_counts(bool multipleApps, bool multipleDocuments)
    {
        await using var h = new Host(); await h.Seed(sync: false); if (multipleDocuments) await h.Seed("second", sync: false);
        await h.App(new(2026, 9, 1)); if (multipleApps) await h.App(new(2026, 9, 1));
        await Assert.ThrowsAsync<ConflictException>(() => h.Sync.SyncAsync());
        await using var db = h.Db.CreateContext(); Assert.Empty(await db.ActivitySources.ToListAsync()); Assert.Empty(await db.Attendances.ToListAsync());
        Assert.Equal(multipleApps ? 2 : 1, await db.Activities.CountAsync()); Assert.Empty(h.Sheets.ManagedWrites);
    }

    [Fact]
    public async Task Never_started_unexported_ready_app_deletes_database_only_and_public_app_key_retry_is_noop()
    {
        await using var h = new Host(); await h.Seed(); await h.Seed("second"); await h.List();
        var app = await h.App(new(2026, 9, 1), ActivityStatus.Ready);
        await using (var db = h.Db.CreateContext()) { var a = await db.Activities.SingleAsync(a => a.Id == app.Id); a.SheetExportedAt = null; await db.SaveChangesAsync(); }
        var before = await Snapshot(h); var writes = h.Sheets.ManagedWrites.Count;
        await h.Service.DeleteAsync($"app:{app.Id}"); await h.Service.DeleteAsync($"app:{app.Id}");
        Assert.Equal(before, await Snapshot(h)); Assert.Equal(writes, h.Sheets.ManagedWrites.Count); Assert.Empty(h.Sheets.Deletions);
        await using var verify = h.Db.CreateContext(); Assert.False(await verify.Activities.AnyAsync(a => a.Id == app.Id));
        Assert.Equal(2, await verify.Activities.CountAsync()); Assert.Equal(AdminAction.ActivityCancel, (await verify.AuditLogs.SingleAsync()).Action);
    }

    [Theory]
    [InlineData(ActivityStatus.AttendanceOpen, false, false)]
    [InlineData(ActivityStatus.ActivityClosed, true, false)]
    [InlineData(ActivityStatus.Ready, false, true)]
    [InlineData(ActivityStatus.Ready, true, false)]
    public async Task Unbound_started_or_exported_app_cannot_delete_same_date_unrelated_source(ActivityStatus status, bool exported, bool opened)
    {
        await using var h = new Host(); await h.Seed(); var row = await h.Completed();
        var app = await h.App(row.Date, status);
        await using (var db = h.Db.CreateContext())
        {
            var a = await db.Activities.SingleAsync(a => a.Id == app.Id);
            a.SheetExportedAt = exported ? DateTime.UtcNow : null; a.AttendanceOpenedAt = opened ? DateTime.UtcNow : null; await db.SaveChangesAsync();
        }
        var before = await Snapshot(h); var writes = h.Sheets.ManagedWrites.Count;
        await Assert.ThrowsAsync<ConflictException>(() => h.Service.DeleteAsync($"app:{app.Id}"));
        Assert.Equal(before, await Snapshot(h)); Assert.Equal(writes, h.Sheets.ManagedWrites.Count); Assert.Empty(h.Sheets.Deletions);
        await using var verify = h.Db.CreateContext(); Assert.True(await verify.Activities.AnyAsync(a => a.Id == app.Id));
        Assert.True(await verify.ActivitySources.AnyAsync(s => s.Id == row.Key)); Assert.Empty(await verify.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task Bound_public_app_key_deletion_retry_is_noop_without_extra_audit()
    {
        await using var h = new Host(); await h.Seed(); await h.List(); var app = await h.App(new(2026, 10, 1));
        await h.Service.EditAsync($"app:{app.Id}", new(app.ActivityDate));
        await h.Service.DeleteAsync($"app:{app.Id}"); await h.Service.DeleteAsync($"app:{app.Id}");
        await using var db = h.Db.CreateContext(); Assert.False(await db.Activities.AnyAsync(a => a.Id == app.Id));
        Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.Action == AdminAction.ActivityCancel)); Assert.Single(h.Sheets.Deletions);
    }
}
