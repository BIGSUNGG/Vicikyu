using Microsoft.EntityFrameworkCore;
using VolleyballClub.Application.Abstractions;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Domain;
using VolleyballClub.Domain.Entities;
using VolleyballClub.Domain.Enums;
using VolleyballClub.Infrastructure.Persistence;

namespace VolleyballClub.Infrastructure.Services;

/// <summary>시트 ↔ DB 동기화 유스케이스(설계: Design/sheet-formats.md).
/// 가져오기(시작 시 1회): 등록된 출석 시트 → 탭별 파싱 → 탭별 부원 매핑으로 Attendance 캐싱,
/// 이미 있는 날짜는 스킵(멱등), 미지정 라벨은 경고.
/// 내보내기: SheetExportedAt이 null인 마감 활동을 학기 탭에 기록(날짜 일치 열 우선, 없으면 삽입).</summary>
public sealed class SheetSyncService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ISheetClient sheetClient,
    ISheetFormatResolver resolver,
    TimeProvider timeProvider) : ISheetSyncService
{
    public async Task<SheetSyncReport> SyncAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var sheets = await db.SheetDefinitions
            .Where(s => s.Kind == SheetKind.Attendance)
            .ToListAsync(ct);
        if (sheets.Count == 0)
        {
            return new SheetSyncReport(0, 0, 0, 0, ["등록된 출석 시트가 없습니다 — 관리자 설정에서 등록하세요."]);
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

                foreach (var column in format.Parse(tab).Activities)
                {
                    // 멱등: 해당 날짜 활동이 이미 있으면 스킵. 단 시트 유래 활동인데 출석이 0개면
                    // 아직 매핑이 없던 것 — 매핑된 출석을 백필한다(부원 연결 후 다음 동기화에 반영).
                    // ponytail: 같은 날짜 2열(하루 2세션)은 첫 열만 가져온다 — 과거 데이터는 1일 1회 가정
                    var existing = await db.Activities
                        .Include(a => a.Attendances)
                        .FirstOrDefaultAsync(a => a.ActivityDate == column.Date, ct);

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

                    var now2 = KstClock.NowUtc(timeProvider);
                    if (existing is not null)
                    {
                        if (existing.CreatedByUserId == Activity.SheetImportCreator
                            && existing.SheetExportedAt is not null
                            && existing.Attendances.Count == 0
                            && userIds.Count > 0)
                        {
                            foreach (var userId in userIds)
                            {
                                existing.Attendances.Add(new Attendance
                                {
                                    UserId = userId,
                                    AttendanceType = AttendanceType.Sheet,
                                    CheckedAt = now2,
                                    CreatedAt = now2,
                                });
                            }

                            importedAttendances += userIds.Count;
                            importedActivities++; // 백필된 활동으로 집계(증거 로그용)
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
            }
        }

        var exportedColumns = await ExportPendingAsync(db, warnings, ct);
        await db.SaveChangesAsync(ct);

        return new SheetSyncReport(importedActivities, importedAttendances, skippedUnmatched, exportedColumns, warnings);
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
    }
}
