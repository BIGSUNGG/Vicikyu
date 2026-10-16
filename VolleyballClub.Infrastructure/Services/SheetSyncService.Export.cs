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
        var target = live ? format.BuildLiveColumn(new SheetTab(tab.Name, tab.Rows), activity.ActivityDate, labels)
            : format.BuildColumn(new SheetTab(tab.Name, tab.Rows), activity.ActivityDate, labels);
        if (target is null) return false;
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
        await ManagedSheetEdits.CommitAsync(db, sheetClient, sheet.SpreadsheetId, tab,
            new ManagedSheetMutation(Guid.NewGuid().ToString("N"), insert ? SheetMutationKind.Insert : SheetMutationKind.Write,
                column, source.Id, cells), () =>
            {
                if (db.Entry(source).State == EntityState.Detached) db.ActivitySources.Add(source);
                source.ActivityId = activity.Id; source.Date = activity.ActivityDate; source.Kind = kind; source.Reason = null;
                if (!live) activity.SheetExportedAt = KstClock.NowUtc(timeProvider);
                return Task.CompletedTask;
            });
        return true;
    }
}
