using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Application.Exceptions;
using VolleyballClub.Domain.Entities;
using VolleyballClub.Domain.Enums;
using VolleyballClub.Infrastructure.Services;

namespace VolleyballClub.Tests;

public sealed partial class AdminActivityManagementTests
{
    private static async Task<string> Snapshot(Host h) => JsonSerializer.Serialize(await h.Sheets.GetManagedTabAsync("first", "2026-2"));

    [Fact]
    public async Task Import_parses_managed_snapshot_not_stale_initial_values()
    {
        await using var h = new Host(); await h.Seed();
        h.Sheets.BeforeManagedRead = (_, _) =>
        {
            h.Sheets.BeforeManagedRead = null;
            var rows = h.Sheets.Documents["first"][0].Rows.Select(r => r.ToList()).ToList();
            rows[1][1] = "26. 09. 02. 수"; rows[2][1] = "출석";
            h.Sheets.SetTab("first", new("2026-2", rows));
        };
        var completed = await h.Completed();
        Assert.Equal(new DateOnly(2026, 9, 2), completed.Date);
        Assert.Equal(1, completed.AttendanceCount);
        await using var db = h.Db.CreateContext();
        Assert.Equal(completed.Key, (await db.ActivitySources.SingleAsync(s => s.ActivityId != null)).Id);
        Assert.Equal(completed.Date, (await db.Activities.SingleAsync()).ActivityDate);
    }

    [Fact]
    public async Task Import_changed_precommit_snapshot_rolls_back_date_and_attendance_reassignment()
    {
        await using var h = new Host(); await h.Seed(); var original = await h.Completed();
        await h.Service.SetAttendanceAsync(original.Key, "a", new(true));
        var rows = h.Sheets.Documents["first"][0].Rows.Select(r => r.ToList()).ToList();
        rows[1][1] = "26. 09. 02. 수"; rows[2][1] = "";
        h.Sheets.SetTab("first", new("2026-2", rows));
        var reads = 0;
        h.Sheets.BeforeManagedRead = (_, _) =>
        {
            if (++reads != 2) return;
            h.Sheets.BeforeManagedRead = null;
            rows[1][1] = "26. 09. 03. 목";
            h.Sheets.SetTab("first", new("2026-2", rows));
        };
        await Assert.ThrowsAsync<ConflictException>(() => h.Sync.SyncAsync());
        await using var db = h.Db.CreateContext();
        var source = await db.ActivitySources.SingleAsync(s => s.Id == original.Key);
        Assert.Equal(original.Date, source.Date); Assert.Equal(original.ActivityId, source.ActivityId);
        Assert.Equal(original.Date, (await db.Activities.SingleAsync()).ActivityDate);
        Assert.Equal("a", (await db.Attendances.SingleAsync()).UserId);
    }

    [Fact]
    public async Task Duplicate_source_keys_in_one_tab_reject_before_any_domain_import()
    {
        await using var h = new Host(); await h.Seed();
        var tab = await h.Sheets.GetManagedTabAsync("first", "2026-2");
        h.Sheets.SetManagedTab("first", tab with { ColumnKeys = new Dictionary<int, string> { [2] = "duplicate", [3] = "duplicate" } });
        await Assert.ThrowsAsync<ConflictException>(() => h.Sync.SyncAsync());
        Assert.Empty(h.Sheets.ManagedWrites);
        await using var db = h.Db.CreateContext();
        Assert.Empty(await db.Activities.ToListAsync()); Assert.Empty(await db.ActivitySources.ToListAsync());
        Assert.Empty(await db.Attendances.ToListAsync()); Assert.Empty(await db.SheetDayMarks.ToListAsync());
    }

    [Fact]
    public async Task Lost_response_with_unrelated_human_edit_refuses_rollback_and_retains_journal()
    {
        await using var h = new Host(); await h.Seed(); await h.List();
        h.Sheets.AfterManagedApply = _ =>
        {
            var rows = h.Sheets.Documents["first"][0].Rows.Select(r => r.ToList()).ToList();
            rows[2][1] = "human edit";
            h.Sheets.SetTab("first", new("2026-2", rows));
            throw new IOException("lost response");
        };
        var error = await Assert.ThrowsAsync<AggregateException>(() => h.Service.CreateAsync(new(h.SheetId, "2026-2", new(2026, 10, 2), SheetDayKind.Scheduled)));
        Assert.Contains(error.InnerExceptions, e => e is ConflictException);
        Assert.Equal(0, h.Sheets.RestoreCount);
        Assert.Equal("human edit", h.Sheets.Documents["first"][0].Rows[2][1]);
        await using (var db = h.Db.CreateContext())
        { Assert.Single(await db.SheetEditJournals.ToListAsync()); Assert.Equal(3, await db.ActivitySources.CountAsync()); Assert.Empty(await db.AuditLogs.ToListAsync()); }
        h.Sheets.AfterManagedApply = null;
        await Assert.ThrowsAsync<ConflictException>(() => h.Sync.ResyncAsync());
        await using var verify = h.Db.CreateContext(); Assert.Single(await verify.SheetEditJournals.ToListAsync());
    }

    [Theory]
    [InlineData(SheetDayKind.NoActivity)]
    [InlineData(SheetDayKind.Scheduled)]
    public async Task Otp_checkin_during_remote_edit_survives_with_conflict_and_exact_sheet_restore(SheetDayKind kind)
    {
        await using var h = new Host(); await h.Seed(); await h.List();
        var app = await h.App(new(2026, 10, 1), ActivityStatus.AttendanceOpen);
        var attached = await h.Service.EditAsync($"app:{app.Id}", new(app.ActivityDate));
        var before = await Snapshot(h);
        var member = new FakeCurrentUser("a");
        var attendance = new AttendanceService(h.Db.Factory, member, new AuditLogService(h.Db.Factory, member, h.Clock),
            new SheetFormatResolver(SemesterTabFormat.All()), h.Clock, h.Service);
        Task<DateTime>? checkin = null;
        h.Sheets.AfterManagedApply = _ =>
        {
            checkin = attendance.CheckInWithOtpAsync("1234");
            // A separate attendance commit gate must permit a real OTP write during remote I/O.
            checkin.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        };
        await Assert.ThrowsAsync<ConflictException>(() => h.Service.EditAsync(attached.Key, new(app.ActivityDate, kind)));
        Assert.NotNull(checkin); Assert.True(checkin.IsCompletedSuccessfully);
        Assert.Equal(before, await Snapshot(h)); Assert.Equal(1, h.Sheets.RestoreCount);
        await using var db = h.Db.CreateContext();
        var saved = await db.Activities.SingleAsync(a => a.Id == app.Id);
        Assert.Equal(ActivityStatus.AttendanceOpen, saved.Status); Assert.Equal("1234", saved.OtpCode);
        var mark = await db.Attendances.SingleAsync(); Assert.Equal(app.Id, mark.ActivityId);
        Assert.Equal("a", mark.UserId); Assert.Equal(AttendanceType.Otp, mark.AttendanceType);
        var source = await db.ActivitySources.SingleAsync(s => s.ActivityId == saved.Id);
        Assert.Equal(SheetDayKind.Scheduled, source.Kind); Assert.Equal(app.ActivityDate, source.Date);
        Assert.Equal(attached.Key, $"app:{saved.Id}"); Assert.Empty(await db.SheetEditJournals.ToListAsync());
    }

    [Theory]
    [InlineData(SheetDayKind.NoActivity)]
    [InlineData(SheetDayKind.Scheduled)]
    public async Task Unmapped_sheet_attendance_blocks_state_demotion_even_with_zero_database_count(SheetDayKind kind)
    {
        await using var h = new Host(); await h.Seed(map: false);
        var rows = h.Sheets.Documents["first"][0].Rows.Select(r => r.ToList()).ToList(); rows[2][1] = "출석";
        h.Sheets.SetTab("first", new("2026-2", rows));
        var completed = await h.Completed(); Assert.Equal(0, completed.AttendanceCount);
        var before = await Snapshot(h); var writes = h.Sheets.ManagedWrites.Count;
        await Assert.ThrowsAsync<ConflictException>(() => h.Service.EditAsync(completed.Key, new(completed.Date, kind)));
        Assert.Equal(before, await Snapshot(h)); Assert.Equal(writes, h.Sheets.ManagedWrites.Count);
        await using var db = h.Db.CreateContext(); Assert.Single(await db.Activities.ToListAsync()); Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    [Theory]
    [InlineData("출석")]
    [InlineData("=IF(1=1,\"출석\",\"\")")]
    public async Task Live_resync_then_closed_export_preserves_unmapped_raw_mark_and_total_formula(string unmapped)
    {
        await using var h = new Host(); await h.Seed(); await h.List();
        var app = await h.App(new(2026, 10, 1), ActivityStatus.AttendanceOpen);
        await h.Service.EditAsync($"app:{app.Id}", new(app.ActivityDate));
        var tab = await h.Sheets.GetManagedTabAsync("first", "2026-2");
        var column = tab.ColumnKeys.Single(k => k.Key > 4).Key - 1;
        var rows = tab.Rows.Select(r => r.ToList()).ToList();
        rows.Insert(3, Enumerable.Repeat("", column + 1).ToList()); rows[3][0] = "Unmapped"; rows[3][column] = unmapped;
        rows[4][column] = "=COUNTIF($E$3:$E$4,\"출석\")";
        h.Sheets.SetTab("first", new("2026-2", rows));
        var before = await h.Sheets.GetManagedTabAsync("first", "2026-2");
        var report = await h.Sync.ResyncAsync(); Assert.Empty(report.Warnings);
        var live = await h.Sheets.GetManagedTabAsync("first", "2026-2");
        Assert.Equal(before.RawValues[3][column], live.RawValues[3][column]);
        Assert.Equal(before.RawValues[4][column], live.RawValues[4][column]);
        await using (var db = h.Db.CreateContext())
        { var activity = await db.Activities.SingleAsync(a => a.Id == app.Id); activity.Status = ActivityStatus.ActivityClosed; activity.SheetExportedAt = null; await db.SaveChangesAsync(); }
        Assert.True(await h.Sync.ExportClosedActivityAsync(app.Id));
        var closed = await h.Sheets.GetManagedTabAsync("first", "2026-2");
        Assert.Equal(before.RawValues[3][column], closed.RawValues[3][column]);
        Assert.Equal(before.RawValues[4][column], closed.RawValues[4][column]);
        Assert.Equal("활동 완료", closed.Rows[0][column]);
    }

    [Fact]
    public async Task Missing_mapping_rejects_attendance_before_any_database_only_change()
    {
        await using var h = new Host(); await h.Seed(map: false); var row = await h.Completed();
        var before = await Snapshot(h); var writes = h.Sheets.ManagedWrites.Count;
        await Assert.ThrowsAsync<ConflictException>(() => h.Service.SetAttendanceAsync(row.Key, "a", new(true)));
        Assert.Equal(before, await Snapshot(h)); Assert.Equal(writes, h.Sheets.ManagedWrites.Count);
        await using var db = h.Db.CreateContext(); Assert.Empty(await db.Attendances.ToListAsync());
        Assert.Empty(await db.AuditLogs.ToListAsync()); Assert.Empty(await db.SheetEditJournals.ToListAsync());
    }

    [Theory]
    [InlineData("시험(중간)")]
    [InlineData("(시험)")]
    [InlineData("(시험(중간))")]
    public async Task Nested_and_outer_parentheses_reason_round_trip_exactly_with_korean_state(string reason)
    {
        await using var h = new Host(); await h.Seed(); await h.List();
        var row = await h.Service.CreateAsync(new(h.SheetId, "2026-2", new(2026, 10, 2), SheetDayKind.NoActivity, reason));
        var imported = (await h.List()).Single(r => r.Key == row.Key);
        Assert.Equal(reason, imported.Reason); Assert.Equal($"활동 없음({reason})", imported.StateText);
        Assert.Equal(row, imported);
    }

    [Fact]
    public async Task Linked_sheet_deletion_is_explicit_conflict_and_preserves_sources_and_audit()
    {
        await using var h = new Host(); await h.Seed(); await h.List();
        var service = new SheetAdminService(h.Db.Factory, h.Current, new AuditLogService(h.Db.Factory, h.Current, h.Clock));
        await Assert.ThrowsAsync<ConflictException>(() => service.DeleteAsync(h.SheetId));
        await using var db = h.Db.CreateContext(); Assert.Single(await db.SheetDefinitions.ToListAsync());
        Assert.Equal(3, await db.ActivitySources.CountAsync()); Assert.Single(await db.Activities.ToListAsync()); Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unbound_app_deletion_never_silently_deletes_database_only(bool ambiguous)
    {
        await using var h = new Host(); await h.Seed(); if (ambiguous) await h.Seed("second"); await h.List();
        var app = await h.App(new(2026, 10, 1)); var before = await Snapshot(h);
        await Assert.ThrowsAsync<ConflictException>(() => h.Service.DeleteAsync($"app:{app.Id}"));
        Assert.Equal(before, await Snapshot(h)); Assert.Empty(h.Sheets.Deletions);
        await using var db = h.Db.CreateContext(); Assert.True(await db.Activities.AnyAsync(a => a.Id == app.Id)); Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task Actual_sheet_read_failure_surfaces_without_empty_list_or_domain_changes()
    {
        await using var h = new Host(); await h.Seed();
        var failure = new IOException("actual managed read failure");
        h.Sheets.BeforeManagedRead = (_, _) => throw failure;
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => h.List()));
        await using var db = h.Db.CreateContext(); Assert.Empty(await db.ActivitySources.ToListAsync()); Assert.Empty(await db.Activities.ToListAsync());
    }

    [Theory]
    [InlineData("create", false)]
    [InlineData("edit", false)]
    [InlineData("delete", false)]
    [InlineData("create", true)]
    [InlineData("edit", true)]
    [InlineData("delete", true)]
    public async Task Audit_or_late_projection_failure_rolls_back_entire_create_edit_delete_and_restores_sheet(string operation, bool lateProjectionFailure)
    {
        await using var h = new Host(); await h.Seed(); var completed = await h.Completed();
        await h.Service.SetAttendanceAsync(completed.Key, "a", new(true));
        var before = await Snapshot(h);
        await using (var db = h.Db.CreateContext())
            await db.Database.ExecuteSqlRawAsync(lateProjectionFailure
                ? "CREATE TRIGGER fail_audit BEFORE INSERT ON SheetDayMarks BEGIN SELECT RAISE(ABORT,'synthetic projection failure'); END;"
                : "CREATE TRIGGER fail_audit BEFORE INSERT ON AuditLogs BEGIN SELECT RAISE(ABORT,'synthetic audit failure'); END;");
        Func<Task> mutate = operation switch
        {
            "create" => () => h.Service.CreateAsync(new(h.SheetId, "2026-2", new(2026, 10, 2), SheetDayKind.Scheduled)),
            "edit" => () => h.Service.EditAsync(completed.Key, new(new(2026, 9, 2))),
            _ => () => h.Service.DeleteAsync(completed.Key)
        };
        await Assert.ThrowsAsync<DbUpdateException>(mutate);
        Assert.Equal(before, await Snapshot(h));
        await using (var db = h.Db.CreateContext())
        {
            Assert.Equal(3, await db.ActivitySources.CountAsync()); Assert.Equal(completed.Date, (await db.Activities.SingleAsync()).ActivityDate);
            Assert.Single(await db.Attendances.ToListAsync()); Assert.Equal(AdminAction.AttendanceAdd, (await db.AuditLogs.SingleAsync()).Action);
            Assert.Empty(await db.SheetEditJournals.ToListAsync()); Assert.Equal(2, await db.SheetDayMarks.CountAsync());
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_audit;");
        }
        await mutate();
        await using var verify = h.Db.CreateContext();
        var actions = await verify.AuditLogs.OrderBy(a => a.Id).Select(a => a.Action).ToListAsync();
        Assert.Equal(new[] { AdminAction.AttendanceAdd, operation == "create" ? AdminAction.ActivityCreate : operation == "edit" ? AdminAction.SettingChange : AdminAction.ActivityCancel }, actions);
        Assert.Equal(operation == "delete" ? 0 : 1, await verify.Attendances.CountAsync());
    }
}
