using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Application.Exceptions;
using VolleyballClub.Domain.Entities;
using VolleyballClub.Domain.Enums;
using VolleyballClub.Infrastructure.Identity;
using VolleyballClub.Infrastructure.Services;

namespace VolleyballClub.Tests;

public sealed partial class AdminActivityManagementTests
{
    private sealed class Host : IAsyncDisposable
    {
        public TestDb Db { get; } = new();
        public FakeSheetClient Sheets { get; } = new();
        public FakeCurrentUser Current { get; } = new("admin", true);
        public FixedTimeProvider Clock { get; } = new(new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc));
        public SheetSyncService Sync { get; }
        public AdminActivityService Service { get; }
        public int SheetId { get; private set; }
        public Host()
        {
            var formats = new SheetFormatResolver(SemesterTabFormat.All());
            Sync = new(Db.Factory, Sheets, formats, Clock);
            Service = new(Db.Factory, Sheets, formats, Current, Sync, Clock);
        }
        public async Task<int> Seed(string document = "first", string tab = "2026-2", bool map = true, bool sync = true)
        {
            await using var db = Db.CreateContext();
            foreach (var id in new[] { "admin", "a", "unmapped" })
                if (!await db.Users.AnyAsync(u => u.Id == id)) db.Users.Add(new ApplicationUser
                { Id = id, UserName = id, StudentNumber = id, Name = id, Status = MemberStatus.Active });
            var sheet = new SheetDefinition { Name = document, SpreadsheetId = document, Kind = SheetKind.Attendance };
            db.SheetDefinitions.Add(sheet); await db.SaveChangesAsync();
            if (map) db.SheetMemberMappings.Add(new SheetMemberMapping { SheetDefinitionId = sheet.Id, TabName = tab, UserId = "a", RowLabel = "A" });
            await db.SaveChangesAsync();
            Sheets.Documents[document] = [new SheetTab(tab,
            [ ["활동 상태", "활동 완료", "활동 예정", "활동 없음(시험)"],
              ["부원", "26. 09. 01. 화", "26. 09. 08. 화", "26. 09. 15. 화"],
              ["A", "", "", ""], ["총 참여자 수", "=COUNTIF(B3:B3,\"출석\")"] ])];
            if (document == "first") SheetId = sheet.Id;
            // 프로덕션의 기동 동기화(Program.cs SyncAsync) 모델 — 시드 후 시트→DB 를 반영한다.
            // GetActivitiesAsync는 더 이상 조회마다 임포트하지 않는다(기동·재동기화 시에만).
            // sync:false는 첫 임포트 시점 자체가 검증 대상인 테스트(충돌·부분 실패)용.
            if (sync) await Sync.SyncAsync();
            return sheet.Id;
        }
        public Task<IReadOnlyList<ManagedActivityDto>> List() => Service.GetActivitiesAsync();
        public async Task<ManagedActivityDto> Completed() => (await List()).Single(a => a.Kind == SheetDayKind.Completed);
        public async Task<Activity> App(DateOnly date, ActivityStatus status = ActivityStatus.ActivityClosed)
        {
            await using var db = Db.CreateContext();
            var a = new Activity { ActivityDate = date, Status = status, CreatedByUserId = "admin", SheetExportedAt = DateTime.UtcNow, OtpCode = "1234" };
            db.Activities.Add(a); await db.SaveChangesAsync(); return a;
        }
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    [Fact]
    public async Task All_operations_require_admin_before_sheet_access()
    {
        await using var h = new Host(); h.Current.IsAdmin = false;
        var date = new DateOnly(2026, 10, 2);
        Func<Task>[] calls = [() => h.Service.GetTargetsAsync(), () => h.List(),
            () => h.Service.CreateAsync(new(1, "2026-2", date, SheetDayKind.Scheduled)),
            () => h.Service.EditAsync("x", new(date)), () => h.Service.DeleteAsync("x"),
            () => h.Service.GetMembersAsync("x"), () => h.Service.SetAttendanceAsync("x", "a", new(true)),
            () => h.Service.GetMemberActivitiesAsync("a")];
        foreach (var call in calls) await Assert.ThrowsAsync<ForbiddenException>(call);
        Assert.Empty(h.Sheets.ManagedWrites);
    }

    [Fact]
    public async Task Targets_and_list_keep_every_document_date_and_same_day_app_session()
    {
        await using var h = new Host(); await h.Seed(); await h.Seed("second");
        h.Sheets.Documents["second"].Add(new SheetTab("회비", []));
        await h.List(); // Bind imported sources before creating intentional independent same-day sessions.
        await h.App(new(2026, 9, 1)); await h.App(new(2026, 9, 1));
        Assert.Equal(2, (await h.Service.GetTargetsAsync()).Count);
        var rows = await h.List();
        Assert.Equal(8, rows.Count); Assert.Equal(8, rows.Select(r => r.Key).Distinct().Count());
        Assert.Equal(4, rows.Count(r => r.Date == new DateOnly(2026, 9, 1)));
        Assert.Equal(rows.Select(r => r.Key), (await h.List()).Select(r => r.Key));
        Assert.All(rows.Where(r => r.ActivityId == null), r => Assert.False(r.CanEditAttendance));
        Assert.All(rows.Where(r => r.Kind == SheetDayKind.Completed), r => Assert.True(r.CanEditAttendance));
        Assert.Contains(rows, r => r.Reason == "시험");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("장소 휴관")]
    public async Task Create_none_reason_round_trips_and_scheduled_never_becomes_current(string? reason)
    {
        await using var h = new Host(); await h.Seed(); await h.List();
        var date = new DateOnly(2026, 10, 2);
        var none = await h.Service.CreateAsync(new(h.SheetId, "2026-2", date, SheetDayKind.NoActivity, reason));
        var same = await h.Service.CreateAsync(new(h.SheetId, "2026-2", date, SheetDayKind.NoActivity, reason));
        Assert.Equal(none.Key, same.Key);
        var scheduled = await h.Service.CreateAsync(new(h.SheetId, "2026-2", date.AddDays(1), SheetDayKind.Scheduled));
        Assert.Null(scheduled.ActivityId);
        Assert.Equal(reason, (await h.List()).Single(r => r.Key == none.Key).Reason);
        await using var db = h.Db.CreateContext();
        Assert.False(await db.Activities.AnyAsync(a => a.Status != ActivityStatus.ActivityClosed));
        Assert.Equal(1, await db.Activities.CountAsync());
        await Assert.ThrowsAsync<ConflictException>(() => h.Service.SetAttendanceAsync(none.Key, "a", new(true)));
        await Assert.ThrowsAsync<ConflictException>(() => h.Service.SetAttendanceAsync(scheduled.Key, "a", new(true)));
    }

    [Fact]
    public async Task Legacy_tabs_can_round_trip_scheduled_and_optional_none_reason()
    {
        await using var h = new Host(); await h.Seed(tab: "2026-1");
        h.Sheets.SetTab("first", new SheetTab("2026-1", [["부원", "26. 03. 01. 일"], ["A", ""]]));
        var row = await h.Service.CreateAsync(new(h.SheetId, "2026-1", new(2026, 4, 1), SheetDayKind.NoActivity, "휴관"));
        Assert.Equal("휴관", (await h.List()).Single(r => r.Key == row.Key).Reason);
        var edited = await h.Service.EditAsync(row.Key, new(new(2026, 4, 2), SheetDayKind.Scheduled));
        Assert.Equal(SheetDayKind.Scheduled, edited.Kind); Assert.Null(edited.Reason);
        Assert.Equal(SheetDayKind.Scheduled, (await h.List()).Single(r => r.Key == row.Key).Kind);
    }

    [Fact]
    public async Task Attendance_date_edits_resync_preserve_identity_unmapped_attendance_and_other_sources()
    {
        await using var h = new Host(); await h.Seed(); var row = await h.Completed();
        await h.Service.SetAttendanceAsync(row.Key, "a", new(true));
        await Assert.ThrowsAsync<ConflictException>(() => h.Service.SetAttendanceAsync(row.Key, "unmapped", new(true)));
        // A historical attendance predating the mapping requirement must still survive imports.
        await using (var seed = h.Db.CreateContext())
        {
            seed.Attendances.Add(new Attendance { ActivityId = row.ActivityId!.Value, UserId = "unmapped", AttendanceType = AttendanceType.Admin });
            await seed.SaveChangesAsync();
        }
        await h.Service.SetAttendanceAsync(row.Key, "a", new(true)); // retry
        await Assert.ThrowsAsync<ConflictException>(() => h.Service.EditAsync(row.Key, new(row.Date, SheetDayKind.NoActivity)));
        await using (var db = h.Db.CreateContext()) Assert.Equal(2, await db.Attendances.CountAsync());
        var edited = await h.Service.EditAsync(row.Key, new(new(2026, 9, 2)));
        var resynced = (await h.List()).Single(r => r.Key == row.Key);
        Assert.Equal(edited, resynced); Assert.Equal(row.ActivityId, edited.ActivityId); Assert.Equal(2, edited.AttendanceCount);
        var member = await h.Service.GetMemberActivitiesAsync("unmapped");
        Assert.Equal(1, member.TotalCount); Assert.Equal(1, member.SemesterCount); Assert.Equal(0, member.MonthCount);
        // 매핑 없는 부원에게는 실제 출석한 활동 행만 나타난다(비회원 학기의 미출석 행 제외)
        Assert.Single(member.Items); Assert.True(member.Items.Single(r => r.Key == row.Key).IsAttended);
        await using var verify = h.Db.CreateContext();
        Assert.All(await verify.Attendances.ToListAsync(), a => Assert.Equal(AttendanceType.Admin, a.AttendanceType));
    }

    [Fact]
    public async Task Team_removal_needs_confirmation_and_old_historical_service_uses_same_sync_path()
    {
        await using var h = new Host(); await h.Seed(); var row = await h.Completed();
        var attendance = new AttendanceService(h.Db.Factory, h.Current, new AuditLogService(h.Db.Factory, h.Current, h.Clock),
            new SheetFormatResolver(SemesterTabFormat.All()), h.Clock, h.Service, h.Sync);
        await attendance.AdminAddForActivityAsync("a", row.ActivityId!.Value);
        await using (var db = h.Db.CreateContext())
        {
            var team = new Team { ActivityId = row.ActivityId.Value, Name = "A" }; team.Members.Add(new TeamMember { UserId = "a" });
            db.Teams.Add(team); await db.SaveChangesAsync();
        }
        var details = await h.Service.GetMemberActivitiesAsync("a");
        Assert.True(details.Items.Single(r => r.Key == row.Key).HasTeam);
        await Assert.ThrowsAsync<ConflictException>(() => attendance.AdminRemoveForActivityAsync("a", row.ActivityId.Value));
        await attendance.AdminRemoveForActivityAsync("a", row.ActivityId.Value, true);
        await h.List();
        await using var verify = h.Db.CreateContext(); Assert.Empty(await verify.Attendances.ToListAsync()); Assert.Empty(await verify.TeamMembers.ToListAsync());
        Assert.Equal(2, await verify.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task Delete_source_column_cascades_only_activity_dependents_and_is_retry_safe()
    {
        await using var h = new Host(); await h.Seed(); var row = await h.Completed();
        await h.Service.SetAttendanceAsync(row.Key, "a", new(true));
        await using (var db = h.Db.CreateContext())
        {
            db.Guests.Add(new Guest { ActivityId = row.ActivityId!.Value, Name = "합성 게스트" });
            var team = new Team { ActivityId = row.ActivityId.Value, Name = "A" }; team.Members.Add(new TeamMember { UserId = "a" });
            db.Teams.Add(team);
            db.SemesterPeriods.Add(new SemesterPeriod { Name = "보존", StartDate = new(2026, 8, 1), EndDate = new(2027, 1, 31) });
            await db.SaveChangesAsync();
        }
        await h.Service.DeleteAsync(row.Key); await h.Service.DeleteAsync(row.Key);
        Assert.Equal(2, (await h.List()).Count);
        await using var verify = h.Db.CreateContext();
        Assert.Equal(3, await verify.Users.CountAsync()); Assert.Equal(1, await verify.SemesterPeriods.CountAsync());
        Assert.Equal(2, await verify.AuditLogs.CountAsync());
        Assert.Contains(await verify.AuditLogs.ToListAsync(), a => a.Action == AdminAction.ActivityCancel);
        Assert.Empty(await verify.Activities.ToListAsync());
        Assert.Empty(await verify.Attendances.ToListAsync()); Assert.Empty(await verify.Guests.ToListAsync()); Assert.Empty(await verify.Teams.ToListAsync());
        Assert.Empty(await verify.TeamMembers.ToListAsync()); Assert.Single(h.Sheets.Deletions);
        Assert.Equal(2, (await h.Sheets.GetManagedTabAsync("first", "2026-2")).ColumnKeys.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sheet_failure_before_or_after_apply_restores_exact_formula_preimage_and_retry(bool afterApply)
    {
        await using var h = new Host(); await h.Seed(); await h.List();
        var before = JsonSerializer.Serialize(await h.Sheets.GetManagedTabAsync("first", "2026-2"));
        Action<ManagedSheetMutation> fail = _ => throw new IOException("synthetic sheet failure");
        if (afterApply) h.Sheets.AfterManagedApply = fail; else h.Sheets.BeforeManagedApply = fail;
        var request = new ActivityCreateRequest(h.SheetId, "2026-2", new(2026, 10, 2), SheetDayKind.NoActivity);
        await Assert.ThrowsAsync<IOException>(() => h.Service.CreateAsync(request));
        Assert.Equal(before, JsonSerializer.Serialize(await h.Sheets.GetManagedTabAsync("first", "2026-2")));
        await using (var db = h.Db.CreateContext()) { Assert.Empty(await db.SheetEditJournals.ToListAsync()); Assert.Equal(3, await db.ActivitySources.CountAsync()); }
        h.Sheets.AfterManagedApply = h.Sheets.BeforeManagedApply = null;
        var created = await h.Service.CreateAsync(request);
        Assert.Equal(created.Key, (await h.List()).Single(r => r.Date == request.Date).Key);
        Assert.Equal(4, (await h.List()).Count);
    }

    [Fact]
    public async Task Database_failure_compensates_column_and_raw_formulas_before_retry()
    {
        await using var h = new Host(); await h.Seed(); await h.List();
        var before = JsonSerializer.Serialize(await h.Sheets.GetManagedTabAsync("first", "2026-2"));
        await using (var db = h.Db.CreateContext()) await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_source BEFORE INSERT ON ActivitySources BEGIN SELECT RAISE(ABORT,'synthetic db failure'); END;");
        var request = new ActivityCreateRequest(h.SheetId, "2026-2", new(2026, 10, 2), SheetDayKind.Scheduled);
        await Assert.ThrowsAsync<DbUpdateException>(() => h.Service.CreateAsync(request));
        Assert.Equal(before, JsonSerializer.Serialize(await h.Sheets.GetManagedTabAsync("first", "2026-2")));
        await using (var db = h.Db.CreateContext()) { Assert.Empty(await db.SheetEditJournals.ToListAsync()); await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_source;"); }
        await h.Service.CreateAsync(request); Assert.Equal(4, (await h.List()).Count);
    }

    [Fact]
    public async Task Failed_compensation_keeps_durable_journal_and_blocks_sync_until_recovered()
    {
        await using var h = new Host(); await h.Seed(); await h.List();
        var before = JsonSerializer.Serialize(await h.Sheets.GetManagedTabAsync("first", "2026-2"));
        h.Sheets.AfterManagedApply = _ => throw new IOException("lost response");
        h.Sheets.BeforeRestore = () => throw new IOException("restore unavailable");
        await Assert.ThrowsAsync<AggregateException>(() => h.Service.CreateAsync(new(h.SheetId, "2026-2", new(2026, 10, 2), SheetDayKind.Scheduled)));
        await using (var db = h.Db.CreateContext()) Assert.Single(await db.SheetEditJournals.ToListAsync());
        await Assert.ThrowsAsync<IOException>(() => h.Sync.ResyncAsync());
        h.Sheets.BeforeRestore = null; h.Sheets.AfterManagedApply = null;
        await h.Sync.ResyncAsync();
        // Recovery restores the preimage; resync then only reads already-bound sources.
        Assert.Equal(before, JsonSerializer.Serialize(await h.Sheets.GetManagedTabAsync("first", "2026-2")));
        await using var verify = h.Db.CreateContext(); Assert.Empty(await verify.SheetEditJournals.ToListAsync()); Assert.Equal(3, await verify.ActivitySources.CountAsync());
    }

    [Fact]
    public async Task Cancellation_does_not_abort_necessary_compensation()
    {
        await using var h = new Host(); await h.Seed(); await h.List(); using var cts = new CancellationTokenSource();
        var before = JsonSerializer.Serialize(await h.Sheets.GetManagedTabAsync("first", "2026-2"));
        h.Sheets.AfterManagedApply = _ => { cts.Cancel(); throw new OperationCanceledException(cts.Token); };
        await Assert.ThrowsAsync<OperationCanceledException>(() => h.Service.CreateAsync(new(h.SheetId, "2026-2", new(2026, 10, 2), SheetDayKind.Scheduled), cts.Token));
        Assert.Equal(before, JsonSerializer.Serialize(await h.Sheets.GetManagedTabAsync("first", "2026-2")));
    }

    [Fact]
    public async Task Source_date_moves_and_external_deletion_do_not_join_other_app_sessions_or_drop_unmapped_members()
    {
        await using var h = new Host(); await h.Seed(); var row = await h.Completed();
        await using (var seed = h.Db.CreateContext())
        {
            seed.Attendances.Add(new Attendance { ActivityId = row.ActivityId!.Value, UserId = "unmapped", AttendanceType = AttendanceType.Admin });
            await seed.SaveChangesAsync();
        }
        var app = await h.App(row.Date);
        var original = h.Sheets.Documents["first"][0]; var rows = original.Rows.Select(r => r.ToList()).ToList();
        rows[1][1] = "26. 09. 02. 수";
        h.Sheets.SetTab("first", new SheetTab("2026-2", rows));
        await h.Sync.SyncAsync();
        var moved = (await h.List()).Single(r => r.Key == row.Key);
        Assert.Equal(new DateOnly(2026, 9, 2), moved.Date); Assert.Equal(row.ActivityId, moved.ActivityId);
        var tab = await h.Sheets.GetManagedTabAsync("first", "2026-2");
        await h.Sheets.ApplyManagedMutationAsync("first", tab, new("external-delete", SheetMutationKind.Delete, 2, row.Key, []));
        await h.Sync.SyncAsync();
        var remaining = await h.List();
        Assert.Contains(remaining, r => r.ActivityId == app.Id && r.Date == row.Date);
        Assert.Contains(remaining, r => r.ActivityId == row.ActivityId && r.SourceLabel.Contains("원본 삭제") && !r.CanEditAttendance);
        await using var db = h.Db.CreateContext(); Assert.Single(await db.Attendances.Where(a => a.UserId == "unmapped").ToListAsync());
    }

    [Fact]
    public async Task Ambiguous_app_source_requires_explicit_selection_and_preserves_live_status_when_only_date_changes()
    {
        await using var h = new Host(); await h.Seed(); var secondId = await h.Seed("second"); await h.List();
        var app = await h.App(new(2026, 10, 1), ActivityStatus.TeamCreated);
        await Assert.ThrowsAsync<ConflictException>(() => h.Service.SetAttendanceAsync($"app:{app.Id}", "a", new(true)));
        var beforeFirst = JsonSerializer.Serialize(await h.Sheets.GetManagedTabAsync("first", "2026-2"));
        var edited = await h.Service.EditAsync($"app:{app.Id}", new(new(2026, 10, 2), SheetId: secondId, TabName: "2026-2"));
        Assert.Equal($"app:{app.Id}", edited.Key); Assert.Equal(ActivityStatus.TeamCreated, edited.Status);
        await h.Service.SetAttendanceAsync(edited.Key, "a", new(true));
        await h.Sync.ResyncAsync();
        Assert.Equal(beforeFirst, JsonSerializer.Serialize(await h.Sheets.GetManagedTabAsync("first", "2026-2")));
        await using var db = h.Db.CreateContext(); var saved = await db.Activities.SingleAsync(a => a.Id == app.Id);
        Assert.Equal(ActivityStatus.TeamCreated, saved.Status); Assert.Equal("1234", saved.OtpCode);
    }

    [Fact]
    public async Task Resync_and_mutation_share_serialization_boundary()
    {
        await using var h = new Host(); await h.Seed(); var row = await h.Completed();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        h.Sheets.BeforeManagedApply = m => { if (m.Kind == SheetMutationKind.Write) { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); } };
        var edit = Task.Run(() => h.Service.SetAttendanceAsync(row.Key, "a", new(true)));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        var resync = Task.Run(() => h.Sync.ResyncAsync());
        try { Assert.False(resync.IsCompleted); } finally { release.Set(); }
        await Task.WhenAll(edit, resync);
        await using var db = h.Db.CreateContext(); Assert.Single(await db.Attendances.ToListAsync());
    }
}
