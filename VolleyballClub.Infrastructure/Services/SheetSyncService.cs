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
public sealed class SheetSyncService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ISheetClient sheetClient,
    ISheetFormatResolver resolver,
    TimeProvider timeProvider) : ISheetSyncService
{
    public async Task<SheetSyncReport> SyncAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var (importedActivities, importedAttendances, skippedUnmatched, warnings) = await ImportAllAsync(db, ct);
        var exportedColumns = await ExportPendingAsync(db, warnings, ct);
        await db.SaveChangesAsync(ct);

        return new SheetSyncReport(importedActivities, importedAttendances, skippedUnmatched, exportedColumns, warnings);
    }

    public async Task<SheetSyncReport> ResyncAsync(CancellationToken ct = default)
    {
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

    /// <summary>등록된 전 출석 시트를 읽어 시트 → DB로 재반영한다(시트가 원천).
    /// 완료 열: 같은 날짜 활동이 있으면 출석 병합(시트에 없는 출석 제거·추가, 유지 행은 시각·타입 보존),
    /// 없으면 마감 활동으로 생성. 예정/없음 열은 캘린더 마크로만 저장한다.</summary>
    private async Task<(int ImportedActivities, int ImportedAttendances, int SkippedUnmatched, List<string> Warnings)>
        ImportAllAsync(ApplicationDbContext db, CancellationToken ct)
    {
        var sheets = await db.SheetDefinitions
            .Where(s => s.Kind == SheetKind.Attendance)
            .ToListAsync(ct);
        if (sheets.Count == 0)
        {
            return (0, 0, 0, ["등록된 출석 시트가 없습니다 — 관리자 설정에서 등록하세요."]);
        }

        var importedActivities = 0;
        var importedAttendances = 0;
        var skippedUnmatched = 0;
        var warnings = new List<string>();
        var now = KstClock.NowUtc(timeProvider);

        foreach (var sheet in sheets)
        {
            List<SheetTab> tabs;
            List<SheetMemberMapping> mappings;
            try
            {
                tabs = [.. await sheetClient.GetTabsAsync(sheet.SpreadsheetId, ct: ct)];
                mappings = await db.SheetMemberMappings
                    .Where(m => m.SheetDefinitionId == sheet.Id)
                    .ToListAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 잘못된 시트 하나가 다른 시트 가져오기까지 막지 않도록 격리한다(내보내기와 대칭)
                warnings.Add($"시트 '{sheet.Name}'({sheet.SpreadsheetId}) 읽기 실패: {ex.Message}");
                continue;
            }

            foreach (var tab in tabs)
            {
                var format = resolver.ResolveByTabName(tab.Name);
                if (format is null)
                {
                    warnings.Add($"탭 '{tab.Name}': 해석 전략 없음 — 건너뜀(회비 등 비출석 탭이면 정상)");
                    continue;
                }

                var labelToUser = mappings
                    .Where(m => m.TabName == tab.Name)
                    .ToDictionary(m => m.RowLabel, m => m.UserId);

                var parsed = format.Parse(tab);
                foreach (var column in parsed.Activities)
                {
                    // 예정/없음 날은 캘린더 표시용 마크로 저장한다.
                    // 시트가 원천: 그날에 시트 유래 마감 활동이 남아 있으면 제거한다(완료→없음/예정 되돌림 반영 —
                    // 남겨두면 캘린더에서 마감 활동 미출석이 마크보다 우선해 활동 없음이 안 보인다).
                    // 앱에서 만든 활동(OTP 실적)은 지우지 않고 경고만 남긴다.
                    if (column.Kind != SheetDayKind.Completed)
                    {
                        var mark = await db.SheetDayMarks.FirstOrDefaultAsync(m => m.Date == column.Date, ct);
                        if (mark is null)
                        {
                            db.SheetDayMarks.Add(new SheetDayMark { Date = column.Date, Kind = column.Kind });
                        }
                        else
                        {
                            mark.Kind = column.Kind;
                        }

                        var dayActivity = await db.Activities
                            .Include(a => a.Attendances)
                            .FirstOrDefaultAsync(a => a.ActivityDate == column.Date, ct);
                        if (dayActivity is not null)
                        {
                            if (dayActivity.CreatedByUserId == Activity.SheetImportCreator)
                            {
                                db.Activities.Remove(dayActivity);
                                warnings.Add($"{tab.Name} {column.Date:yyyy-MM-dd}: 없음/예정 표시로 시트 유래 활동을 제거했습니다");
                            }
                            else
                            {
                                warnings.Add($"{tab.Name} {column.Date:yyyy-MM-dd}: 앱 활동이 있어 없음/예정 표기를 병합하지 않습니다");
                            }
                        }

                        continue;
                    }

                    // ponytail: 같은 날짜 2열(하루 2세션)은 순차 병합되어 마지막 열이 이긴다 — 과거 데이터는 1일 1회 가정
                    var existing = await db.Activities
                        .Include(a => a.Attendances)
                        .FirstOrDefaultAsync(a => a.ActivityDate == column.Date, ct);
                    // 이 탭에 매핑된 부원 — 시트가 표현할 수 있는 사용자(매핑 없는 부원은 시트가 못 보므로 출석을 지우지 않는다)
                    var mappedUserIds = labelToUser.Values.ToHashSet();

                    var userIds = new List<string>();
                    foreach (var label in column.AttendedRowLabels)
                    {
                        if (labelToUser.TryGetValue(label, out var userId))
                        {
                            if (!userIds.Contains(userId))
                            {
                                userIds.Add(userId);
                            }
                        }
                        else
                        {
                            skippedUnmatched++;
                            warnings.Add(
                                $"{tab.Name} {column.Date:yyyy-MM-dd}: '{label}' 미지정 — 부원 관리에서 연결하면 다음 동기화부터 반영");
                        }
                    }

                    if (existing is not null)
                    {
                        if (existing.Status != ActivityStatus.ActivityClosed)
                        {
                            // 진행 중 활동 날짜가 시트에서 완료로 표시됨 — 병합은 마감 후에만(활동 중 출석 보호)
                            warnings.Add($"{tab.Name} {column.Date:yyyy-MM-dd}: 진행 중 활동과 시트 완료 표기가 다릅니다 — 마감 후 반영됩니다");
                            continue;
                        }

                        // 시트가 원천: 시트 표시와 다른 출석을 병합한다(수기 수정 반영).
                        // 유지되는 출석은 원래 시각·타입을 보존하고, 추가분만 Sheet 타입으로 넣는다.
                        // 제거는 이 탭에 매핑된 부원만 — 미매핑 부원은 시트에 행이 없어 표현 불가능하므로 지우지 않는다.
                        var current = existing.Attendances.ToDictionary(a => a.UserId);
                        var changed = false;
                        foreach (var userId in userIds.Where(id => !current.ContainsKey(id)))
                        {
                            existing.Attendances.Add(new Attendance
                            {
                                UserId = userId,
                                AttendanceType = AttendanceType.Sheet,
                                CheckedAt = now,
                                CreatedAt = now,
                            });
                            importedAttendances++;
                            changed = true;
                        }

                        foreach (var stale in existing.Attendances
                                 .Where(a => !userIds.Contains(a.UserId) && mappedUserIds.Contains(a.UserId))
                                 .ToList())
                        {
                            existing.Attendances.Remove(stale);
                            changed = true;
                        }

                        if (changed)
                        {
                            importedActivities++; // 갱신된 활동으로 집계
                        }

                        continue;
                    }

                    var activity = new Activity
                    {
                        ActivityDate = column.Date,
                        Status = ActivityStatus.ActivityClosed,
                        AttendanceOpenedAt = now,
                        AttendanceClosedAt = now,
                        ActivityClosedAt = now,
                        CreatedByUserId = Activity.SheetImportCreator,
                        CreatedAt = now,
                        UpdatedAt = now,
                        // 이미 시트에 있는 열이므로 내보내기 대상에서 제외
                        SheetExportedAt = now,
                    };
                    foreach (var userId in userIds)
                    {
                        activity.Attendances.Add(new Attendance
                        {
                            UserId = userId,
                            AttendanceType = AttendanceType.Sheet,
                            CheckedAt = now,
                            CreatedAt = now,
                        });
                    }

                    db.Activities.Add(activity);
                    importedActivities++;
                    importedAttendances += userIds.Count;
                }

                // 탭 범위 안에서 시트가 더 이상 예정/없음으로 표시하지 않는 날의 마크를 제거한다
                // (예정→완료 전환·열 삭제 — 시트가 마크의 원천이므로 전체 상태를 조정)
                var keepDates = parsed.Activities
                    .Where(c => c.Kind != SheetDayKind.Completed)
                    .Select(c => c.Date)
                    .ToList();
                var staleMarks = await db.SheetDayMarks
                    .Where(m => m.Date >= format.From && m.Date <= format.To && !keepDates.Contains(m.Date))
                    .ToListAsync(ct);
                db.SheetDayMarks.RemoveRange(staleMarks);
            }
        }

        return (importedActivities, importedAttendances, skippedUnmatched, warnings);
    }

    /// <summary>진행 중 활동의 현재 출석을 시트에 임시 기록한다(신형 상태행 '활동 예정', 마감 시 완료로 재기록).</summary>
    private async Task ExportLiveAsync(ApplicationDbContext db, Activity activity, List<string> warnings, CancellationToken ct)
    {
        var format = resolver.ResolveByDate(activity.ActivityDate);
        if (format is null)
        {
            return; // 기록할 학기 탭 범위가 아님
        }

        var sheet = await db.SheetDefinitions.FirstOrDefaultAsync(s => s.Kind == SheetKind.Attendance, ct);
        if (sheet is null)
        {
            return;
        }

        var tabs = await sheetClient.GetTabsAsync(sheet.SpreadsheetId, [format.TabName], ct);
        if (tabs.Count == 0)
        {
            throw new InvalidOperationException($"탭 '{format.TabName}'을(를) 읽을 수 없습니다.");
        }

        var attendedUserIds = await db.Attendances
            .Where(a => a.ActivityId == activity.Id)
            .Select(a => a.UserId)
            .ToHashSetAsync(ct);
        var mappings = await db.SheetMemberMappings
            .Where(m => m.SheetDefinitionId == sheet.Id && m.TabName == format.TabName)
            .ToListAsync(ct);
        var attendedLabels = mappings.Where(m => attendedUserIds.Contains(m.UserId))
            .Select(m => m.RowLabel)
            .ToHashSet();

        var target = format.BuildLiveColumn(tabs[0], activity.ActivityDate, attendedLabels);
        if (target is null)
        {
            return; // 이미 완료/없음으로 확정된 열 — 되돌리지 않는다
        }

        if (target.InsertColumn)
        {
            await sheetClient.InsertColumnAsync(sheet.SpreadsheetId, format.TabName, target.ColumnIndex, target.Column, ct);
        }
        else
        {
            await sheetClient.WriteColumnAsync(sheet.SpreadsheetId, format.TabName, target.ColumnIndex, target.Column, ct);
        }

        if (target.StatusColumn is not null)
        {
            await sheetClient.WriteColumnAsync(sheet.SpreadsheetId, format.TabName, target.ColumnIndex, target.StatusColumn, ct);
        }
    }

    public async Task<bool> ExportClosedActivityAsync(int activityId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var activity = await db.Activities.FirstAsync(a => a.Id == activityId, ct);
        if (activity.SheetExportedAt is not null)
        {
            return true; // 이미 기록됨(다시 열기 후 재마감 포함 — 중복 열 방지)
        }

        try
        {
            await ExportActivityAsync(db, activity, null, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false; // 마감 롤백 금지 — 다음 시작 SyncAsync가 복구(원인은 그때 경고로 기록)
        }

        activity.SheetExportedAt = KstClock.NowUtc(timeProvider);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<int> ExportPendingAsync(ApplicationDbContext db, List<string> warnings, CancellationToken ct)
    {
        var pending = await db.Activities
            .Where(a => a.Status == ActivityStatus.ActivityClosed && a.SheetExportedAt == null)
            .OrderBy(a => a.ActivityDate)
            .ToListAsync(ct);

        var exported = 0;
        foreach (var activity in pending)
        {
            try
            {
                await ExportActivityAsync(db, activity, warnings, ct);
                activity.SheetExportedAt = KstClock.NowUtc(timeProvider);
                exported++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add($"활동 {activity.ActivityDate:yyyy-MM-dd} 시트 내보내기 실패: {ex.Message}");
            }
        }

        return exported;
    }

    private async Task ExportActivityAsync(ApplicationDbContext db, Activity activity, List<string>? warnings, CancellationToken ct)
    {
        var format = resolver.ResolveByDate(activity.ActivityDate)
            ?? throw new InvalidOperationException(
                $"활동 날짜 {activity.ActivityDate:yyyy-MM-dd}를 커버하는 학기 전략이 없습니다.");
        var sheet = await db.SheetDefinitions.FirstOrDefaultAsync(s => s.Kind == SheetKind.Attendance, ct)
            ?? throw new InvalidOperationException("등록된 출석 시트가 없습니다.");
        // ponytail: 출석 시트가 여러 개면 첫 번째에 기록한다 — 다중 시트 구분이 필요해지면 전략에 시트 지정 추가

        var tabs = await sheetClient.GetTabsAsync(sheet.SpreadsheetId, [format.TabName], ct);
        var tab = tabs.Count > 0
            ? tabs[0]
            : throw new InvalidOperationException($"탭 '{format.TabName}'을(를) 읽을 수 없습니다.");

        var attendedUserIds = await db.Attendances
            .Where(a => a.ActivityId == activity.Id)
            .Select(a => a.UserId)
            .ToHashSetAsync(ct);
        var mappings = await db.SheetMemberMappings
            .Where(m => m.SheetDefinitionId == sheet.Id && m.TabName == format.TabName)
            .ToListAsync(ct);
        var attendedLabels = mappings.Where(m => attendedUserIds.Contains(m.UserId))
            .Select(m => m.RowLabel)
            .ToHashSet();
        if (warnings is not null)
        {
            var unmapped = attendedUserIds.Except(mappings.Select(m => m.UserId)).Count();
            if (unmapped > 0)
            {
                warnings.Add($"활동 {activity.ActivityDate:yyyy-MM-dd}: 시트 연결 미지정 부원 {unmapped}명 — 시트에 빈칸으로 기록됨");
            }
        }

        var target = format.BuildColumn(tab, activity.ActivityDate, attendedLabels);
        if (target.InsertColumn)
        {
            await sheetClient.InsertColumnAsync(sheet.SpreadsheetId, format.TabName, target.ColumnIndex, target.Column, ct);
        }
        else
        {
            await sheetClient.WriteColumnAsync(sheet.SpreadsheetId, format.TabName, target.ColumnIndex, target.Column, ct);
        }

        // 신형 서식: 상태행(1행)을 활동 완료로 갱신 — 기존 열 덮어쓰기 시 헤더(2행)를 건너뛴 분리 쓰기
        if (target.StatusColumn is not null)
        {
            await sheetClient.WriteColumnAsync(sheet.SpreadsheetId, format.TabName, target.ColumnIndex, target.StatusColumn, ct);
        }

        // 시트에서 완료로 전환됐으므로 그 날의 예정/없음 마크를 즉시 제거(다음 동기화 전 캘린더 정합성)
        var dayMark = await db.SheetDayMarks.FirstOrDefaultAsync(m => m.Date == activity.ActivityDate, ct);
        if (dayMark is not null)
        {
            db.SheetDayMarks.Remove(dayMark);
        }
    }

    public async Task<bool> ExportScheduledActivityAsync(int activityId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        try
        {
            var activity = await db.Activities.AsNoTracking().FirstAsync(a => a.Id == activityId, ct);
            var format = resolver.ResolveByDate(activity.ActivityDate);
            if (format is null)
            {
                return true; // 기록할 학기 탭 범위가 아님 — 활동 시작은 계속된다
            }

            var sheet = await db.SheetDefinitions.FirstOrDefaultAsync(s => s.Kind == SheetKind.Attendance, ct);
            if (sheet is null)
            {
                return true; // 등록된 출석 시트가 없음 — 마감 내보내기가 정리한다
            }

            var tabs = await sheetClient.GetTabsAsync(sheet.SpreadsheetId, [format.TabName], ct);
            if (tabs.Count == 0)
            {
                return false;
            }

            // 활동 시작 직후 — 아직 출석자가 없으므로 마크 없이 상태행 '활동 예정'만 기록
            var target = format.BuildLiveColumn(tabs[0], activity.ActivityDate, new HashSet<string>());
            if (target is null)
            {
                return true; // 구형 탭(상태 행 없음) 또는 이미 완료/없음으로 확정된 열
            }

            if (target.InsertColumn)
            {
                await sheetClient.InsertColumnAsync(sheet.SpreadsheetId, format.TabName, target.ColumnIndex, target.Column, ct);
            }
            else
            {
                await sheetClient.WriteColumnAsync(sheet.SpreadsheetId, format.TabName, target.ColumnIndex, target.Column, ct);
                if (target.StatusColumn is not null)
                {
                    await sheetClient.WriteColumnAsync(sheet.SpreadsheetId, format.TabName, target.ColumnIndex, target.StatusColumn, ct);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false; // 예정 기록은 부가 정보 — 복구 대상 아님(마감 내보내기가 최종 상태를 쓴다)
        }

        return true;
    }
}
