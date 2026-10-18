using Microsoft.EntityFrameworkCore;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Application.Exceptions;
using VolleyballClub.Domain;
using VolleyballClub.Domain.Entities;
using VolleyballClub.Domain.Enums;
using VolleyballClub.Infrastructure.Persistence;

namespace VolleyballClub.Infrastructure.Services;

public sealed partial class SheetSyncService
{
    private async Task<bool> ExportCoreAsync(ApplicationDbContext db, Activity activity, bool live, CancellationToken ct)
    {
        var revision = activity.SheetSyncRevision;
        var source = await db.ActivitySources.SingleOrDefaultAsync(s => s.ActivityId == activity.Id, ct);
        var format = source is null ? resolver.ResolveByDate(activity.ActivityDate) : resolver.ResolveByTabName(source.TabName);
        if (format is null) throw new RuleViolationException("활동 날짜를 지원하는 시트 탭이 없습니다.");
        SheetDefinition sheet;
        if (source is not null) sheet = await db.SheetDefinitions.SingleAsync(s => s.Id == source.SheetDefinitionId, ct);
        else
        {
            var candidates = new List<SheetDefinition>();
            foreach (var candidate in await db.SheetDefinitions.Where(s => s.Kind == SheetKind.Attendance).ToListAsync(ct))
                if ((await sheetClient.GetTabsAsync(candidate.SpreadsheetId, ct: ct)).Any(t => t.Name == format.TabName)) candidates.Add(candidate);
            if (candidates.Count != 1) throw new ConflictException("활동의 대상 시트/탭을 유일하게 정할 수 없습니다.");
            sheet = candidates.Single();
        }
        var tab = await sheetClient.GetManagedTabAsync(sheet.SpreadsheetId, format.TabName, ct);
        var ids = await db.Attendances.Where(a => a.ActivityId == activity.Id).Select(a => a.UserId).ToHashSetAsync(ct);
        var mappings = await db.SheetMemberMappings.Where(m => m.SheetDefinitionId == sheet.Id && m.TabName == format.TabName).ToListAsync(ct);
        var labels = mappings.Where(m => ids.Contains(m.UserId)).Select(m => m.RowLabel).ToHashSet();
        var wasBound = source is not null;
        if (source is null)
        {
            var scheduled = await db.ActivitySources.Where(s => s.SheetDefinitionId == sheet.Id && s.TabName == tab.Name
                && s.Date == activity.ActivityDate && s.Kind == SheetDayKind.Scheduled && s.ActivityId == null).ToListAsync(ct);
            if (scheduled.Count > 1) throw new ConflictException("같은 날짜의 예정 원본이 여러 개입니다.");
            source = scheduled.SingleOrDefault() ?? new ActivitySource
            { SheetDefinitionId = sheet.Id, TabName = tab.Name, Date = activity.ActivityDate };
        }
        var existingColumns = tab.ColumnKeys.Where(k => k.Value == source.Id).Select(k => k.Key).ToList();
        if (existingColumns.Count > 1) throw new ConflictException("활동 원본 열 식별자가 중복입니다.");
        var insert = existingColumns.Count == 0;
        var ownedColumn = insert ? 0 : existingColumns.Single();
        // Date is not identity: ignore other sessions' same-date headers in the format-only view.
        // Reopening may change our own completed column back to a live column, never a sibling's column.
        var view = tab.Rows.Select(r => r.ToList()).ToList();
        var headerRow = view.FirstOrDefault()?.Any(v => v.StartsWith("활동 ")) == true ? 1 : 0;
        if (view.Count > headerRow)
        {
            for (var attempt = 0; attempt < view[headerRow].Count; attempt++)
            {
                var found = format.FindDateColumnIndex(new SheetTab(tab.Name, view), activity.ActivityDate);
                if (found is null || found == ownedColumn || found > view[headerRow].Count) break;
                view[headerRow][found.Value - 1] = "";
            }
            if (live && headerRow == 1 && ownedColumn > 0) view[0][ownedColumn - 1] = "활동 예정";
        }
        var target = live ? format.BuildLiveColumn(new SheetTab(tab.Name, view), activity.ActivityDate, labels)
            : format.BuildColumn(new SheetTab(tab.Name, view), activity.ActivityDate, labels);
        if (target is null) return false;
        var delta = wasBound && (activity.SheetSyncRequiredUserIds.Count > 0
            || activity.SheetSyncPendingAt != null && activity.SheetSyncRevision > 0);
        var requiredIds = delta ? activity.SheetSyncRequiredUserIds.ToHashSet() : ids.Union(activity.SheetSyncRequiredUserIds).ToHashSet();
        var representedLabels = tab.Rows.Skip(headerRow + 1)
            .TakeWhile(r => r.FirstOrDefault()?.Trim() is not ("총 참여자 수" or "탈퇴/퇴출자"))
            .Select(r => r.FirstOrDefault()?.Trim()).ToHashSet();
        var representable = mappings.Where(m => representedLabels.Contains(m.RowLabel)).Select(m => m.UserId).ToHashSet();
        // 마감 활동은 전원 매핑 필요(시트 우선 가져오기가 미기록 출석을 지울 수 있다).
        // 진행 중 활동은 매핑된 부원부터 부분 기록하고 나머지는 계속 펜딩으로 보존한다.
        var blocked = requiredIds.Where(id => !representable.Contains(id)).ToList();
        if (blocked.Count > 0 && (!live || representable.Count == 0))
            throw new ConflictException("출석 변경 부원의 시트 행 매핑이 없거나 행을 찾을 수 없어 미반영 상태를 유지합니다.");
        var column = insert ? target.InsertColumn ? target.ColumnIndex : tab.RawValues.Select(r => r.Count).DefaultIfEmpty(1).Max() + 1 : existingColumns.Single();
        var cells = tab.RawValues.Select(r => !insert && r.Count >= column ? r[column - 1] : "{}").ToList();
        foreach (var part in new[] { target.Column, target.StatusColumn }.OfType<SheetColumn>())
            for (var i = 0; i < part.Cells.Count; i++)
            {
                var row = part.FirstRowIndex - 1 + i;
                var label = row < tab.Rows.Count ? tab.Rows[row].FirstOrDefault()?.Trim() : null;
                var headerRows = tab.Rows.Count > 0 && tab.Rows[0].Any(v => v.StartsWith("활동 ")) ? 2 : 1;
                // Existing sources own mapped member cells and headers, not unmapped marks or total formulas.
                if (!insert && row >= headerRows && !mappings.Any(m => m.RowLabel == label)) continue;
                if (!insert && delta && row >= headerRows
                    && !mappings.Any(m => m.RowLabel == label && requiredIds.Contains(m.UserId))) continue;
                while (cells.Count <= row) cells.Add("{}");
                cells[row] = ManagedSheetEdits.Raw(part.Cells[i]);
            }
        // Existing-column format builders omit the header; a separate session must get its own header.
        if (insert && !target.InsertColumn)
        {
            var headerIndex = tab.Rows.Count > 0 && tab.Rows[0].Any(v => v.StartsWith("활동 ")) ? 1 : 0;
            while (cells.Count <= headerIndex) cells.Add("{}");
            cells[headerIndex] = ManagedSheetEdits.Raw($"{activity.ActivityDate:yy. MM. dd.}");
        }
        if (!live && !(tab.Rows.Count > 0 && tab.Rows[0].Any(v => v.StartsWith("활동 "))))
        {
            if (cells.Count == 0) cells.Add("{}");
            cells[0] = ManagedSheetEdits.Raw($"{activity.ActivityDate:yy. MM. dd.} 활동 완료");
        }
        var kind = live ? SheetDayKind.Scheduled : SheetDayKind.Completed;
        async Task AcknowledgeAsync()
        {
            if (db.Entry(source).State == EntityState.Detached) db.ActivitySources.Add(source);
            source.ActivityId = activity.Id; source.Date = activity.ActivityDate; source.Kind = kind; source.Reason = null;
            await db.Entry(activity).ReloadAsync(CancellationToken.None);
            if (!live) activity.SheetExportedAt = KstClock.NowUtc(timeProvider);
            if (activity.SheetSyncRevision == revision)
            {
                if (live && blocked.Count > 0)
                {
                    // 부분 기록: 이번에 기록한 부원만 요청 목록에서 제거 — 남은 부원은 펜딩 유지.
                    activity.SheetSyncRequiredUserIds.RemoveAll(id => representable.Contains(id));
                }
                else
                {
                    activity.SheetSyncPendingAt = null;
                    activity.SheetSyncError = null;
                    activity.SheetSyncRequiredUserIds.Clear();
                }
            }
        }
        if (!insert && source.Kind == kind
            && cells.SequenceEqual(tab.RawValues.Select(r => r.Count >= column ? r[column - 1] : "{}")))
        {
            if (ManagedSheetEdits.Json(tab) != ManagedSheetEdits.Json(await sheetClient.GetManagedTabAsync(sheet.SpreadsheetId, tab.Name, ct)))
                throw new ConflictException("기록 확인 중 시트가 변경되었습니다. 미반영 상태를 유지합니다.");
            await ManagedSheetEdits.AttendanceCommitGate.WaitAsync(CancellationToken.None);
            try
            {
                await using var transaction = await db.Database.BeginTransactionAsync(CancellationToken.None);
                await AcknowledgeAsync();
                await db.SaveChangesAsync(CancellationToken.None);
                await AdminActivityService.RebuildMarks(db);
                await transaction.CommitAsync(CancellationToken.None);
            }
            finally { ManagedSheetEdits.AttendanceCommitGate.Release(); }
            return false;
        }
        await ManagedSheetEdits.CommitAsync(db, sheetClient, sheet.SpreadsheetId, tab,
            new ManagedSheetMutation(Guid.NewGuid().ToString("N"), insert ? SheetMutationKind.Insert : SheetMutationKind.Write,
                column, source.Id, cells), async () =>
            {
                // CommitAsync holds AttendanceCommitGate; a newer local revision remains pending.
                await AcknowledgeAsync();
            });
        return true;
    }
}
