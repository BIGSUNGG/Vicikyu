using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VolleyballClub.Application.Abstractions;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Application.Exceptions;
using VolleyballClub.Domain.Entities;
using VolleyballClub.Domain.Enums;
using VolleyballClub.Infrastructure.Identity;
using VolleyballClub.Infrastructure.Services;

namespace VolleyballClub.Tests;

public sealed class SheetWriteSafetyTests
{
    private static async Task<SheetSyncHost> CreateAsync()
    {
        var h = new SheetSyncHost(new SemesterTabFormat("2026-2", new(2026, 8, 1), new(2027, 1, 31)));
        await h.SeedSheetAsync();
        await using (var db = h.Db.CreateContext())
        {
            db.Users.AddRange(new ApplicationUser { Id = "a", Name = "A", StudentNumber = "A", Status = MemberStatus.Active },
                new ApplicationUser { Id = "b", Name = "B", StudentNumber = "B", Status = MemberStatus.Active });
            await db.SaveChangesAsync();
        }
        await h.SeedMappingAsync("2026-2", "A", "a");
        await h.SeedMappingAsync("2026-2", "B", "b");
        h.Client.Tabs.Add(new SheetTab("2026-2", [
            ["활동 상태", "활동 완료"], ["부원", "26. 10. 01."], ["A", "미출석"], ["B", "미출석"], ["총 참여자 수", "=COUNTIF(B3:B4,\"출석\")"]]));
        await h.Sync.SyncAsync();
        h.Client.Writes.Clear(); h.Client.ManagedWrites.Clear();
        return h;
    }

    private static async Task<int> AddPendingAsync(SheetSyncHost h, string userId)
    {
        await using var db = h.Db.CreateContext();
        var a = await db.Activities.SingleAsync();
        db.Attendances.Add(new Attendance { ActivityId = a.Id, UserId = userId, AttendanceType = AttendanceType.Admin });
        a.MarkSheetPending(DateTime.UtcNow, userId);
        await db.SaveChangesAsync();
        return a.Id;
    }

    [Fact]
    public async Task GetStatusAsync_classifies_mapping_missing_with_unmapped_member_names()
    {
        await using var h = await CreateAsync();
        int id;
        await using (var db = h.Db.CreateContext())
        {
            db.SheetMemberMappings.Remove(await db.SheetMemberMappings.SingleAsync(m => m.UserId == "b"));
            var a = await db.Activities.SingleAsync();
            db.Attendances.Add(new Attendance { ActivityId = a.Id, UserId = "b", AttendanceType = AttendanceType.Admin });
            a.MarkSheetPending(DateTime.UtcNow, "b");
            await db.SaveChangesAsync(); id = a.Id;
        }

        var status = await h.Sync.GetStatusAsync();

        var item = Assert.Single(status.Pending);
        Assert.Equal(SheetPendingReason.MappingMissing, item.Reason);
        Assert.Equal(h.SheetId, item.SheetId);
        Assert.Equal("2026-2", item.TabName);
        var member = Assert.Single(item.UnmappedMembers!);
        Assert.Equal("b", member.UserId);
        Assert.Equal("B", member.Name);
    }

    [Fact]
    public async Task GetStatusAsync_reports_other_when_every_required_member_is_mapped()
    {
        await using var h = await CreateAsync();
        var id = await AddPendingAsync(h, "a");
        h.Client.BeforeManagedApply = _ => throw new IOException("synthetic credential outage");
        Assert.False((await h.Sync.PushActivityAsync(id)).Success);

        var status = await h.Sync.GetStatusAsync();

        var item = Assert.Single(status.Pending);
        Assert.Equal(SheetPendingReason.Other, item.Reason);
        Assert.Empty(item.UnmappedMembers!);
        Assert.NotNull(item.Error);
    }

    [Fact]
    public async Task Pending_survives_journal_save_failed_delivery_and_retry_before_import()
    {
        await using var h = await CreateAsync();
        var id = await AddPendingAsync(h, "a");
        var sawPendingAtNetworkBoundary = false;
        h.Client.BeforeManagedApply = _ =>
        {
            using var db = h.Db.CreateContext();
            sawPendingAtNetworkBoundary = db.Activities.Single().SheetSyncPendingAt is not null && db.SheetEditJournals.Any();
            throw new IOException("synthetic Sheets outage");
        };
        Assert.False((await h.Sync.PushActivityAsync(id)).Success);
        Assert.True(sawPendingAtNetworkBoundary);
        var failed = await h.Sync.ResyncAsync();
        Assert.Equal(0, failed.ImportedActivities);
        Assert.Contains(failed.Warnings, w => w.Contains("건너뜁니다"));
        await using (var db = h.Db.CreateContext())
        {
            Assert.NotNull((await db.Activities.SingleAsync()).SheetSyncPendingAt);
            Assert.Equal("a", (await db.Attendances.SingleAsync()).UserId);
        }
        h.Client.BeforeManagedApply = null;
        await h.Sync.SyncAsync();
        Assert.Equal(0, (await h.Sync.GetStatusAsync()).PendingCount);
        Assert.Equal("출석", h.Client.Tabs.Single().Rows[2][1]);
        await using var check = h.Db.CreateContext();
        Assert.Equal("a", (await check.Attendances.SingleAsync()).UserId);
    }

    [Fact]
    public async Task Live_partial_write_records_mapped_members_and_keeps_unmapped_pending()
    {
        await using var h = await CreateAsync();
        int id;
        await using (var db = h.Db.CreateContext())
        {
            db.SheetMemberMappings.Remove(await db.SheetMemberMappings.SingleAsync(m => m.UserId == "b"));
            await db.SaveChangesAsync();
        }
        await using (var db = h.Db.CreateContext())
        {
            var now = DateTime.UtcNow;
            var live = new Activity { ActivityDate = new(2026, 10, 2), Status = ActivityStatus.AttendanceOpen, CreatedByUserId = "a",
                CreatedAt = now, UpdatedAt = now, AttendanceOpenedAt = now };
            live.Attendances.Add(new Attendance { UserId = "a", AttendanceType = AttendanceType.Otp, CheckedAt = now, CreatedAt = now });
            live.Attendances.Add(new Attendance { UserId = "b", AttendanceType = AttendanceType.Otp, CheckedAt = now, CreatedAt = now });
            live.MarkSheetPending(now, "a");
            live.MarkSheetPending(now, "b");
            db.Activities.Add(live);
            await db.SaveChangesAsync();
            id = live.Id;
        }

        var first = await h.Sync.PushActivityAsync(id);

        Assert.True(first.Success);
        await using (var db = h.Db.CreateContext())
        {
            var source = await db.ActivitySources.AsNoTracking().SingleAsync(s => s.ActivityId == id);
            var tab = await h.Client.GetManagedTabAsync("ss-test", "2026-2");
            var column = tab.ColumnKeys.Single(k => k.Value == source.Id).Key;
            Assert.Equal("활동 예정", tab.Rows[0][column - 1]);
            Assert.Equal("출석", tab.Rows[2][column - 1]);
            Assert.True(tab.Rows[3].Count < column || tab.Rows[3][column - 1] == "");
        }
        var status = await h.Sync.GetStatusAsync();
        Assert.Equal(1, status.PendingCount);
        Assert.Equal(SheetPendingReason.MappingMissing, status.Pending[0].Reason);
        Assert.Equal("b", status.Pending[0].UnmappedMembers!.Single().UserId);

        await h.SeedMappingAsync("2026-2", "B", "b");
        var second = await h.Sync.PushActivityAsync(id);

        Assert.True(second.Success);
        Assert.Equal(0, (await h.Sync.GetStatusAsync()).PendingCount);
        await using (var db = h.Db.CreateContext())
        {
            var source = await db.ActivitySources.AsNoTracking().SingleAsync(s => s.ActivityId == id);
            var tab = await h.Client.GetManagedTabAsync("ss-test", "2026-2");
            var column = tab.ColumnKeys.Single(k => k.Value == source.Id).Key;
            Assert.Equal("출석", tab.Rows[2][column - 1]);
            Assert.Equal("출석", tab.Rows[3][column - 1]);
            var activity = await db.Activities.SingleAsync(a => a.Id == id);
            Assert.Null(activity.SheetSyncPendingAt);
            Assert.Empty(activity.SheetSyncRequiredUserIds);
        }
    }

    [Fact]
    public async Task Closed_activity_still_requires_full_mapping_before_export()
    {
        await using var h = await CreateAsync();
        int id;
        await using (var db = h.Db.CreateContext())
        {
            db.SheetMemberMappings.Remove(await db.SheetMemberMappings.SingleAsync(m => m.UserId == "b"));
            var a = await db.Activities.SingleAsync();
            a.Attendances.Add(new Attendance { UserId = "b", AttendanceType = AttendanceType.Admin });
            a.MarkSheetPending(DateTime.UtcNow, "b");
            await db.SaveChangesAsync();
            id = a.Id;
        }

        var push = await h.Sync.PushActivityAsync(id);

        Assert.False(push.Success);
        Assert.Contains("매핑", push.Error);
        Assert.Equal(1, (await h.Sync.GetStatusAsync()).PendingCount);
    }

    [Fact]
    public async Task Older_delivery_cannot_acknowledge_a_newer_attendance_commit()
    {
        await using var h = await CreateAsync();
        var id = await AddPendingAsync(h, "a");
        h.Client.AfterManagedApply = _ =>
        {
            h.Client.AfterManagedApply = null;
            AddPendingAsync(h, "b").GetAwaiter().GetResult();
        };
        Assert.True((await h.Sync.PushActivityAsync(id)).Success);
        Assert.Equal(1, (await h.Sync.GetStatusAsync()).PendingCount);
        Assert.Equal("미출석", h.Client.Tabs.Single().Rows[3][1]);
        Assert.True((await h.Sync.PushActivityAsync(id)).Success);
        Assert.Equal(0, (await h.Sync.GetStatusAsync()).PendingCount);
        Assert.Equal("출석", h.Client.Tabs.Single().Rows[3][1]);
    }

    [Fact]
    public async Task Import_rechecks_pending_after_remote_reads_before_touching_database()
    {
        await using var h = await CreateAsync();
        h.Client.BeforeManagedRead = (_, _) =>
        {
            h.Client.BeforeManagedRead = null;
            AddPendingAsync(h, "a").GetAwaiter().GetResult();
        };
        var report = await h.Sync.SyncAsync();
        Assert.Contains(report.Warnings, w => w.Contains("가져오기를 중단"));
        await using var db = h.Db.CreateContext();
        Assert.Equal("a", (await db.Attendances.SingleAsync()).UserId);
        Assert.NotNull((await db.Activities.SingleAsync()).SheetSyncPendingAt);
    }

    [Fact]
    public async Task Offline_admin_attendance_edit_is_committed_before_remote_access()
    {
        await using var h = await CreateAsync();
        var current = new FakeCurrentUser("a", true);
        var service = new AdminActivityService(h.Db.Factory, h.Client, new SheetFormatResolver([h.Format]), current,
            h.Sync, new FixedTimeProvider(DateTime.UtcNow));
        string key;
        await using (var db = h.Db.CreateContext()) key = (await db.ActivitySources.SingleAsync()).Id;
        h.Client.BeforeManagedRead = (_, _) => throw new IOException("offline");
        await service.SetAttendanceAsync(key, "a", new(true));
        Assert.False(h.Sync.LastPushResult!.Success);
        await using var check = h.Db.CreateContext();
        Assert.Equal("a", (await check.Attendances.SingleAsync()).UserId);
        Assert.NotNull((await check.Activities.SingleAsync()).SheetSyncPendingAt);
    }

    [Fact]
    public async Task Missing_mapping_for_a_removed_attendee_keeps_pending_instead_of_claiming_delivery()
    {
        await using var h = await CreateAsync();
        var id = await AddPendingAsync(h, "a");
        Assert.True((await h.Sync.PushActivityAsync(id)).Success);
        await using (var db = h.Db.CreateContext())
        {
            db.Attendances.Remove(await db.Attendances.SingleAsync());
            (await db.Activities.SingleAsync()).MarkSheetPending(DateTime.UtcNow, "a");
            db.SheetMemberMappings.Remove(await db.SheetMemberMappings.SingleAsync(m => m.UserId == "a"));
            await db.SaveChangesAsync();
        }
        Assert.False((await h.Sync.PushActivityAsync(id)).Success);
        Assert.Equal(1, (await h.Sync.GetStatusAsync()).PendingCount);
        Assert.Equal("출석", h.Client.Tabs.Single().Rows[2][1]);
    }

    [Fact]
    public async Task Queued_push_of_an_acknowledged_revision_does_not_overwrite_manual_sheet_marks()
    {
        await using var h = await CreateAsync();
        int id;
        await using (var db = h.Db.CreateContext()) id = (await db.Activities.SingleAsync()).Id;
        var tab = await h.Client.GetManagedTabAsync("ss-test", "2026-2");
        var raw = tab.RawValues.Select(r => r.ToList()).ToList();
        raw[3][1] = ManagedSheetEdits.Raw("출석");
        h.Client.SetManagedTab("ss-test", tab with { RawValues = raw });
        Assert.False((await h.Sync.PushActivityAsync(id)).Written);
        Assert.Empty(h.Client.Writes);
        Assert.Equal("출석", h.Client.Tabs.Single().Rows[3][1]);
    }

    [Fact]
    public async Task Reopen_and_status_only_close_change_our_status_not_clean_manual_attendance()
    {
        await using var h = await CreateAsync();
        var tab = await h.Client.GetManagedTabAsync("ss-test", "2026-2");
        var raw = tab.RawValues.Select(r => r.ToList()).ToList(); raw[3][1] = ManagedSheetEdits.Raw("출석");
        h.Client.SetManagedTab("ss-test", tab with { RawValues = raw });
        int id;
        await using (var db = h.Db.CreateContext())
        {
            var a = await db.Activities.SingleAsync(); id = a.Id;
            a.Status = ActivityStatus.AttendanceOpen; a.MarkSheetPending(DateTime.UtcNow); await db.SaveChangesAsync();
        }
        Assert.True((await h.Sync.PushActivityAsync(id)).Success);
        Assert.Equal("활동 예정", h.Client.Tabs.Single().Rows[0][1]);
        Assert.Equal("출석", h.Client.Tabs.Single().Rows[3][1]);
        await using (var db = h.Db.CreateContext())
        {
            var a = await db.Activities.SingleAsync();
            a.Status = ActivityStatus.ActivityClosed; a.MarkSheetPending(DateTime.UtcNow); await db.SaveChangesAsync();
        }
        Assert.True(await h.Sync.ExportClosedActivityAsync(id));
        Assert.Equal("활동 완료", h.Client.Tabs.Single().Rows[0][1]);
        Assert.Equal("출석", h.Client.Tabs.Single().Rows[3][1]);
    }

    [Fact]
    public async Task Second_same_day_session_writes_its_own_live_column_without_touching_completed_session()
    {
        await using var h = await CreateAsync();
        var before = await h.Client.GetManagedTabAsync("ss-test", "2026-2");
        int id;
        await using (var db = h.Db.CreateContext())
        {
            var a = new Activity { ActivityDate = new(2026, 10, 1), Status = ActivityStatus.AttendanceOpen, CreatedByUserId = "a" };
            a.Attendances.Add(new Attendance { UserId = "a", AttendanceType = AttendanceType.Otp });
            a.MarkSheetPending(DateTime.UtcNow, "a"); db.Activities.Add(a); await db.SaveChangesAsync(); id = a.Id;
        }
        Assert.True((await h.Sync.PushActivityAsync(id)).Success);
        await using var check = h.Db.CreateContext();
        var source = await check.ActivitySources.SingleAsync(s => s.ActivityId == id);
        var after = await h.Client.GetManagedTabAsync("ss-test", "2026-2");
        var index = after.ColumnKeys.Single(k => k.Value == source.Id).Key - 1;
        Assert.Equal("활동 예정", after.Rows[0][index]); Assert.Equal("출석", after.Rows[2][index]);
        var original = after.ColumnKeys.Single(k => k.Value == before.ColumnKeys[2]).Key - 1;
        Assert.Equal(before.Rows[0][1], after.Rows[0][original]); Assert.Equal(before.Rows[2][1], after.Rows[2][original]);
        Assert.Equal(0, (await h.Sync.GetStatusAsync()).PendingCount);
    }

    [Fact]
    public async Task Verified_noop_delivery_still_binds_the_scheduled_source_to_the_app_session()
    {
        await using var h = await CreateAsync();
        var tab = await h.Client.GetManagedTabAsync("ss-test", "2026-2");
        var raw = tab.RawValues.Select(r => r.ToList()).ToList();
        raw[0][1] = ManagedSheetEdits.Raw("활동 예정"); raw[2][1] = ManagedSheetEdits.Raw(""); raw[3][1] = ManagedSheetEdits.Raw("");
        h.Client.SetManagedTab("ss-test", tab with { RawValues = raw });
        await h.Sync.SyncAsync();
        int id;
        await using (var db = h.Db.CreateContext())
        {
            var a = new Activity { ActivityDate = new(2026, 10, 1), Status = ActivityStatus.AttendanceOpen, CreatedByUserId = "a" };
            a.MarkSheetPending(DateTime.UtcNow); db.Activities.Add(a); await db.SaveChangesAsync(); id = a.Id;
        }
        var result = await h.Sync.PushActivityAsync(id);
        Assert.True(result.Success); Assert.False(result.Written);
        await using var check = h.Db.CreateContext();
        Assert.Equal(id, (await check.ActivitySources.SingleAsync()).ActivityId);
        Assert.False(await check.SheetDayMarks.AnyAsync());
        Assert.Null((await check.Activities.SingleAsync()).SheetSyncPendingAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_accepts_only_marker_drift_not_changed_data(bool changedData)
    {
        await using var h = await CreateAsync();
        var before = await h.Client.GetManagedTabAsync("ss-test", "2026-2");
        var mutation = new ManagedSheetMutation("abandoned", SheetMutationKind.Bind, 2, "unused", []);
        var raw = before.RawValues.Select(r => r.ToList()).ToList();
        if (changedData) raw[2][1] = "{\"stringValue\":\"출석\"}";
        h.Client.SetManagedTab("ss-test", before with { LastMutation = "newer-marker", RawValues = raw });
        await using (var db = h.Db.CreateContext())
        {
            db.SheetEditJournals.Add(new SheetEditJournal { Id = mutation.OperationId, SpreadsheetId = "ss-test",
                BeforeJson = JsonSerializer.Serialize(before), MutationJson = JsonSerializer.Serialize(mutation) });
            await db.SaveChangesAsync();
        }
        if (changedData)
            await Assert.ThrowsAsync<ConflictException>(() => ManagedSheetEdits.RecoverAsync(h.Db.Factory, h.Client));
        else await ManagedSheetEdits.RecoverAsync(h.Db.Factory, h.Client);
        Assert.Empty(h.Client.ManagedWrites);
        Assert.Equal(0, h.Client.RestoreCount);
        await using var check = h.Db.CreateContext();
        Assert.Equal(changedData ? 1 : 0, await check.SheetEditJournals.CountAsync());
    }
}
