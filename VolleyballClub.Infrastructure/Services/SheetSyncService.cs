using Microsoft.EntityFrameworkCore;
using VolleyballClub.Application.Abstractions;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Domain;
using VolleyballClub.Domain.Entities;
using VolleyballClub.Domain.Enums;
using VolleyballClub.Infrastructure.Persistence;

namespace VolleyballClub.Infrastructure.Services;

/// <summary>시트 ↔ DB 동기화 유스케이스(설계: Design/sheet-formats.md).
/// 가져오기: 등록된 출석 시트 → 탭별 파싱 → 탭별 부원 매핑으로 Attendance 캐싱.
/// 시트가 원천 — 같은 날짜 활동이 있으면 출석을 병합한다(시트 표시 추가/제거 반영, 유지 행은 시각·타입 보존).
/// 내보내기: SheetExportedAt이 null인 마감 활동을 학기 탭에 기록(날짜 일치 열 우선, 없으면 삽입).
/// ResyncAsync(관리자 '시트 갱신'): 진행 중 활동 현재 출석 임시 기록 → 미내보낸 마감 내보내기 → 전체 재반영.</summary>
public sealed partial class SheetSyncService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ISheetClient sheetClient,
    ISheetFormatResolver resolver,
    TimeProvider timeProvider) : ISheetSyncService
{

    public async Task<SheetSyncReport> SyncAsync(CancellationToken ct = default)
    {
        await ManagedSheetEdits.Gate.WaitAsync(ct);
        try
        {
            await ManagedSheetEdits.RecoverAsync(dbFactory, sheetClient);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var warnings = new List<string>();
        var exportedColumns = await ExportPendingAsync(db, warnings, ct);
        var (importedActivities, importedAttendances, skippedUnmatched, importWarnings) = await ImportAllAsync(db, ct);
        warnings.AddRange(importWarnings);
        await db.SaveChangesAsync(ct);

        return new SheetSyncReport(importedActivities, importedAttendances, skippedUnmatched, exportedColumns, warnings);

        }
        finally { ManagedSheetEdits.Gate.Release(); }
    }

    public async Task<SheetSyncReport> ResyncAsync(CancellationToken ct = default)
    {
        await ManagedSheetEdits.Gate.WaitAsync(ct);
        try
        {
            await ManagedSheetEdits.RecoverAsync(dbFactory, sheetClient);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var warnings = new List<string>();

        // 1) 진행 중 활동의 현재 출석을 임시 기록한다(신형 상태행은 '활동 예정' 유지, 마감 시 완료로 재기록)
        var live = await db.Activities
            .Where(a => a.Status != ActivityStatus.ActivityClosed)
            .OrderBy(a => a.ActivityDate)
            .ToListAsync(ct);
        foreach (var activity in live)
        {
            try
            {
                await ExportLiveAsync(db, activity, warnings, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add($"진행 활동 {activity.ActivityDate:yyyy-MM-dd} 시트 임시 기록 실패: {ex.Message}");
            }
        }

        // 2) 시트에 아직 기록되지 않은 마감 활동 내보내기
        var exportedColumns = await ExportPendingAsync(db, warnings, ct);

        // 3) 시트를 원천으로 전체 재반영(수기 수정이 출석에 그대로 반영된다)
        var (importedActivities, importedAttendances, skippedUnmatched, importWarnings) = await ImportAllAsync(db, ct);
        warnings.AddRange(importWarnings);
        await db.SaveChangesAsync(ct);

        return new SheetSyncReport(importedActivities, importedAttendances, skippedUnmatched, exportedColumns, warnings);

        }
        finally { ManagedSheetEdits.Gate.Release(); }
    }

    /// <summary>등록된 전 출석 시트를 읽어 시트 → DB로 재반영한다(시트가 원천).
    /// 완료 열: 같은 날짜 활동이 있으면 출석 병합(시트에 없는 출석 제거·추가, 유지 행은 시각·타입 보존),
    /// 없으면 마감 활동으로 생성. 예정/없음 열은 캘린더 마크로만 저장한다.</summary>
    private async Task<(int ImportedActivities, int ImportedAttendances, int SkippedUnmatched, List<string> Warnings)>
        ImportAllAsync(ApplicationDbContext db, CancellationToken ct)
    {
        var sheets = await db.SheetDefinitions.Where(s => s.Kind == SheetKind.Attendance).ToListAsync(ct);
        var changed = 0; var added = 0; var unmatched = 0; var warnings = new List<string>();
        var now = KstClock.NowUtc(timeProvider);
        if (sheets.Count == 0) warnings.Add("등록된 출석 시트가 없습니다.");
        // Reconnect exported history only to unbound sources, including retries after a partial import.
        var unboundHistory = await db.Activities.Include(a => a.Attendances)
            .Where(a => !db.ActivitySources.Any(s => s.ActivityId == a.Id)
                && (a.CreatedByUserId == Activity.SheetImportCreator
                    || a.Status == ActivityStatus.ActivityClosed && a.SheetExportedAt != null))
            .ToListAsync(ct);
        // History is eligible only when there is exactly one source candidate across ALL sheets.
        var inputs = new List<(SheetDefinition Sheet, ManagedSheetTab Raw, SheetTab Tab, ISemesterSheetFormat Format, SheetSemesterData Data)>();
        foreach (var sheet in sheets)
        {
            var tabs = await sheetClient.GetTabsAsync(sheet.SpreadsheetId, ct: ct);
            foreach (var tab in tabs)
            {
                var format = resolver.ResolveByTabName(tab.Name);
                if (format is null) { warnings.Add($"탭 '{tab.Name}': 해석 전략 없음"); continue; }
                var raw = await sheetClient.GetManagedTabAsync(sheet.SpreadsheetId, tab.Name, ct);
                var coherent = new SheetTab(raw.Name, raw.Rows);
                inputs.Add((sheet, raw, coherent, format, format.Parse(coherent)));
            }
        }
        var allKeys = inputs.SelectMany(i => i.Raw.ColumnKeys.Values).ToList();
        if (allKeys.Distinct().Count() != allKeys.Count)
            throw new VolleyballClub.Application.Exceptions.ConflictException("원본 열 식별자가 중복되었습니다. 가져오기를 중단했습니다.");
        var existingSources = await db.ActivitySources.AsNoTracking().ToListAsync(ct);
        var unboundDates = inputs.SelectMany(i => i.Data.Activities.Where(c => c.Kind == SheetDayKind.Completed
            && !existingSources.Any(s => s.ActivityId != null && (c.ColumnIndex > 0
                ? i.Raw.ColumnKeys.TryGetValue(c.ColumnIndex, out var key) && s.Id == key
                : s.SheetDefinitionId == i.Sheet.Id && s.TabName == i.Tab.Name && s.Date == c.Date))))
            .Select(c => c.Date).ToHashSet();
        foreach (var history in unboundHistory.Where(a => unboundDates.Contains(a.ActivityDate)).GroupBy(a => a.ActivityDate))
        {
            var columns = inputs.SelectMany(i => i.Data.Activities).Where(c => c.Date == history.Key).ToList();
            if (history.Count() != 1 || columns.Count != 1)
                throw new VolleyballClub.Application.Exceptions.ConflictException($"{history.Key}: 기존 활동 연결이 모호합니다. 원본을 확인하세요.");
        }
        foreach (var (sheet, initial, tab, format, parsed) in inputs)
        {
            var raw = initial;
            // Bind all identities before tracking domain changes; journal saves must not flush a partial import.
            foreach (var column in parsed.Activities.Where(c => c.ColumnIndex > 0))
                if (!raw.ColumnKeys.ContainsKey(column.ColumnIndex))
                {
                    var binding = new ManagedSheetMutation(Guid.NewGuid().ToString("N"), SheetMutationKind.Bind, column.ColumnIndex, Guid.NewGuid().ToString("N"), []);
                    await ManagedSheetEdits.CommitAsync(db, sheetClient, sheet.SpreadsheetId, raw, binding, () => Task.CompletedTask);
                    var bound = await sheetClient.GetManagedTabAsync(sheet.SpreadsheetId, tab.Name, ct);
                    ManagedSheetEdits.RequireOwnedPostimage(raw, bound, binding);
                    raw = bound;
                }
            await ManagedSheetEdits.AttendanceCommitGate.WaitAsync(ct);
            try
            {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var mappings = await db.SheetMemberMappings.Where(m => m.SheetDefinitionId == sheet.Id && m.TabName == tab.Name).ToListAsync(ct);
            var labelToUser = mappings.ToDictionary(m => m.RowLabel, m => m.UserId);
            var mappedIds = mappings.Select(m => m.UserId).ToHashSet();
            var seen = new HashSet<string>();
            foreach (var column in parsed.Activities)
            {
                var source = default(ActivitySource);
                if (column.ColumnIndex > 0)
                {
                    var key = raw.ColumnKeys[column.ColumnIndex];
                    source = await db.ActivitySources.Include(s => s.Activity).ThenInclude(a => a!.Attendances)
                        .SingleOrDefaultAsync(s => s.Id == key, ct);
                    if (source is not null && (source.SheetDefinitionId != sheet.Id || source.TabName != tab.Name))
                        throw new VolleyballClub.Application.Exceptions.ConflictException("원본 열 식별자가 다른 탭에서 중복되었습니다.");
                    source ??= new ActivitySource { Id = key, SheetDefinitionId = sheet.Id, TabName = tab.Name };
                }
                else
                {
                    // Compatibility for format test doubles; production SemesterTabFormat always supplies indices.
                    source = await db.ActivitySources.Include(s => s.Activity).ThenInclude(a => a!.Attendances)
                        .SingleOrDefaultAsync(s => s.SheetDefinitionId == sheet.Id && s.TabName == tab.Name && s.Date == column.Date, ct)
                        ?? new ActivitySource { SheetDefinitionId = sheet.Id, TabName = tab.Name };
                }
                seen.Add(source.Id);
                if (db.Entry(source).State == EntityState.Detached) db.ActivitySources.Add(source);
                if (source.Activity is null && column.Kind == SheetDayKind.Completed)
                {
                    source.Activity = unboundHistory.SingleOrDefault(a => a.ActivityDate == column.Date);
                }
                if (source.Activity is { Status: ActivityStatus.ActivityClosed, SheetExportedAt: null } pending
                    && pending.CreatedByUserId != Activity.SheetImportCreator)
                {
                    warnings.Add($"활동 {pending.ActivityDate:yyyy-MM-dd} 내보내기 대기 중: 이전 시트 값으로 활동/출석을 변경하지 않았습니다.");
                    continue;
                }
                source.IsMissing = false;
                source.Date = column.Date; source.Kind = column.Kind; source.Reason = column.Reason;
                if (source.Activity is { Status: not ActivityStatus.ActivityClosed })
                {
                    // Operational state belongs to the app, not the scheduled status on its live sheet column.
                    source.Activity.ActivityDate = column.Date;
                    continue;
                }
                if (column.Kind != SheetDayKind.Completed)
                {
                    if (source.Activity is { } previous)
                    {
                        if (previous.Attendances.Count > 0)
                            throw new VolleyballClub.Application.Exceptions.ConflictException("출석자가 있는 완료 활동을 없음/예정으로 가져올 수 없습니다.");
                        db.Activities.Remove(previous); source.Activity = null; source.ActivityId = null;
                    }
                    continue;
                }
                var users = column.AttendedRowLabels.Where(labelToUser.ContainsKey).Select(l => labelToUser[l]).ToHashSet();
                unmatched += column.AttendedRowLabels.Count(l => !labelToUser.ContainsKey(l));
                foreach (var label in column.AttendedRowLabels.Where(l => !labelToUser.ContainsKey(l)))
                    warnings.Add($"{tab.Name} {column.Date}: '{label}' 미지정");
                var activityChanged = false;
                if (source.Activity is null)
                {
                    source.Activity = new Activity { ActivityDate = column.Date, Status = ActivityStatus.ActivityClosed,
                        CreatedByUserId = Activity.SheetImportCreator, CreatedAt = now, UpdatedAt = now,
                        AttendanceOpenedAt = now, AttendanceClosedAt = now, ActivityClosedAt = now, SheetExportedAt = now };
                    activityChanged = true;
                }
                var activity = source.Activity;
                if (activity.ActivityDate != column.Date) { activity.ActivityDate = column.Date; activityChanged = true; }
                var current = activity.Attendances.Select(a => a.UserId).ToHashSet();
                foreach (var id in users.Except(current))
                {
                    activity.Attendances.Add(new Attendance { UserId = id, AttendanceType = AttendanceType.Sheet, CheckedAt = now, CreatedAt = now });
                    added++; activityChanged = true;
                }
                foreach (var stale in activity.Attendances.Where(a => mappedIds.Contains(a.UserId) && !users.Contains(a.UserId)).ToList())
                { db.Attendances.Remove(stale); activityChanged = true; }
                if (activityChanged) changed++;
            }
            var removed = await db.ActivitySources.Include(s => s.Activity).ThenInclude(a => a!.Attendances)
                .Where(s => s.SheetDefinitionId == sheet.Id && s.TabName == tab.Name && !seen.Contains(s.Id)).ToListAsync(ct);
            foreach (var source in removed)
            {
                if (source.Activity is { } a)
                {
                    // Deleting the source never destroys app sessions or unrepresented member attendance.
                    if (a.CreatedByUserId == Activity.SheetImportCreator && a.Attendances.All(x => mappedIds.Contains(x.UserId))) db.Activities.Remove(a);
                    else { source.IsMissing = true; continue; }
                }
                db.ActivitySources.Remove(source);
            }
            if (ManagedSheetEdits.Json(raw) != ManagedSheetEdits.Json(await sheetClient.GetManagedTabAsync(sheet.SpreadsheetId, tab.Name, ct)))
                throw new VolleyballClub.Application.Exceptions.ConflictException("가져오는 동안 시트가 변경되었습니다. 다시 시도하세요.");
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            }
            finally { ManagedSheetEdits.AttendanceCommitGate.Release(); }
        }
        await AdminActivityService.RebuildMarks(db);
        return (changed, added, unmatched, warnings);
    }

    private async Task ExportLiveAsync(ApplicationDbContext db, Activity activity, List<string> warnings, CancellationToken ct)
        => await ExportCoreAsync(db, activity, true, ct);

    public async Task<bool> ExportClosedActivityAsync(int activityId, CancellationToken ct = default)
    {
        await ManagedSheetEdits.Gate.WaitAsync(ct);
        try
        {
            await ManagedSheetEdits.RecoverAsync(dbFactory, sheetClient);
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var activity = await db.Activities.SingleAsync(a => a.Id == activityId, ct);
            if (activity.SheetExportedAt is not null) return true;
            try { return await ExportCoreAsync(db, activity, false, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return false; }
        }
        finally { ManagedSheetEdits.Gate.Release(); }
    }

    public async Task<bool> ExportScheduledActivityAsync(int activityId, CancellationToken ct = default)
    {
        await ManagedSheetEdits.Gate.WaitAsync(ct);
        try
        {
            await ManagedSheetEdits.RecoverAsync(dbFactory, sheetClient);
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var activity = await db.Activities.SingleAsync(a => a.Id == activityId, ct);
            try { await ExportCoreAsync(db, activity, true, ct); return true; }
            catch (Exception ex) when (ex is not OperationCanceledException) { return false; }
        }
        finally { ManagedSheetEdits.Gate.Release(); }
    }

    public async Task<bool> RemoveCancelledActivityAsync(int activityId, CancellationToken ct = default)
    {
        await ManagedSheetEdits.Gate.WaitAsync(ct);
        try
        {
            await ManagedSheetEdits.RecoverAsync(dbFactory, sheetClient);
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var source = await db.ActivitySources.SingleOrDefaultAsync(s => s.ActivityId == activityId, ct);
            if (source is null) return true; // An unbound app session never owns a date-matched sheet column.
            var sheet = await db.SheetDefinitions.SingleAsync(s => s.Id == source.SheetDefinitionId, ct);
            var tab = await sheetClient.GetManagedTabAsync(sheet.SpreadsheetId, source.TabName, ct);
            try
            {
                await ManagedSheetEdits.CommitAsync(db, sheetClient, sheet.SpreadsheetId, tab,
                    new ManagedSheetMutation(Guid.NewGuid().ToString("N"), SheetMutationKind.Delete,
                        AdminActivityService.FindSourceColumn(tab, source.Id), source.Id, []), () =>
                    { db.ActivitySources.Remove(source); return Task.CompletedTask; });
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { return false; }
        }
        finally { ManagedSheetEdits.Gate.Release(); }
    }

    private async Task<int> ExportPendingAsync(ApplicationDbContext db, List<string> warnings, CancellationToken ct)
    {
        var pending = await db.Activities.Where(a => a.Status == ActivityStatus.ActivityClosed && a.SheetExportedAt == null)
            .OrderBy(a => a.ActivityDate).Select(a => a.Id).ToListAsync(ct);
        var exported = 0;
        foreach (var id in pending)
        {
            // A failed managed commit clears tracking; reload the next pending activity before exporting it.
            var activity = await db.Activities.SingleAsync(a => a.Id == id, ct);
            try { if (await ExportCoreAsync(db, activity, false, ct)) exported++; }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { warnings.Add($"활동 {activity.ActivityDate:yyyy-MM-dd} 시트 내보내기 실패: {ex.Message}"); }
        }
        return exported;
    }
}
