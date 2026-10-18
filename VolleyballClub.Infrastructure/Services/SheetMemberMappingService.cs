using Microsoft.EntityFrameworkCore;
using VolleyballClub.Application.Abstractions;
using VolleyballClub.Application.Exceptions;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Domain;
using VolleyballClub.Domain.Enums;
using VolleyballClub.Infrastructure.Identity;
using VolleyballClub.Infrastructure.Persistence;

namespace VolleyballClub.Infrastructure.Services;

/// <summary>탭별 부원↔시트 행 연결 관리. 행 후보는 시트를 실시간으로 읽고
/// 라벨의 이름 접두사가 부원 이름과 같으면 추천(최상단 강조)으로 올린다.
/// 연결 변경은 해당 탭에만 적용하며 출석 동기화는 별도로 실행한다.</summary>
public sealed class SheetMemberMappingService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ISheetClient sheetClient,
    ISheetFormatResolver resolver,
    ICurrentUserService currentUser,
    IAuditLogService auditLog,
    TimeProvider time) : ISheetMemberMappingService
{
    public async Task<List<MemberTabMappingDto>> GetForMemberAsync(string userId)
    {
        await currentUser.RequireAdminAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var memberName = await db.Users
            .Where(u => u.Id == userId)
            .Select(u => u.Name)
            .FirstOrDefaultAsync() ?? throw new NotFoundException("부원을 찾을 수 없습니다.");

        var mappings = await db.SheetMemberMappings
            .Where(m => m.UserId == userId)
            .ToListAsync();
        var held = await db.SheetMemberMappings.Where(m => m.UserId != userId)
            .Select(m => new ValueTuple<int, string, string>(m.SheetDefinitionId, m.TabName, m.RowLabel))
            .ToListAsync();

        var tabs = await ReadTabLabelsAsync(db, held);
        return tabs.Select(t => new MemberTabMappingDto(
                t.SheetId,
                t.SheetName,
                t.TabName,
                mappings.FirstOrDefault(m => m.SheetDefinitionId == t.SheetId && m.TabName == t.TabName)?.RowLabel,
                ProjectOptions(t.Labels, memberName)))
            .ToList();
    }

    public async Task<IReadOnlyDictionary<string, List<MemberTabMappingDto>>> GetBulkOptionsAsync(IReadOnlyList<string> names)
    {
        await currentUser.RequireAdminAsync();
        var distinct = names
            .Select(n => n?.Trim() ?? "")
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (distinct.Count == 0) return new Dictionary<string, List<MemberTabMappingDto>>();
        if (distinct.Count > MemberBulkValidation.MaxRows)
            throw new RuleViolationException($"이름은 최대 {MemberBulkValidation.MaxRows}개까지 조회할 수 있습니다.");

        await using var db = await dbFactory.CreateDbContextAsync();
        var held = await db.SheetMemberMappings
            .Select(m => new ValueTuple<int, string, string>(m.SheetDefinitionId, m.TabName, m.RowLabel))
            .ToListAsync();

        var tabs = await ReadTabLabelsAsync(db, held);
        return distinct.ToDictionary(
            name => name,
            name => tabs.Select(t => new MemberTabMappingDto(
                    t.SheetId, t.SheetName, t.TabName, null, ProjectOptions(t.Labels, name)))
                .ToList());
    }

    /// <summary>학기 탭별 연결 가능 행 라벨 — 부원 구간에서 유일하고 held에 없는 것만. 시트를 한 번만 읽는다.</summary>
    private async Task<List<(int SheetId, string SheetName, string TabName, List<string> Labels)>> ReadTabLabelsAsync(
        ApplicationDbContext db,
        IReadOnlyCollection<(int SheetDefinitionId, string TabName, string RowLabel)> held)
    {
        var sheets = await db.SheetDefinitions
            .Where(s => s.Kind == SheetKind.Attendance)
            .OrderBy(s => s.Id)
            .ToListAsync();

        var result = new List<(int, string, string, List<string>)>();
        foreach (var sheet in sheets)
        {
            var tabs = await sheetClient.GetTabsAsync(sheet.SpreadsheetId);
            foreach (var tab in tabs)
            {
                // 학기 전략이 있는 탭(출석 탭)만 부원 행을 가진다
                if (resolver.ResolveByTabName(tab.Name) is null)
                {
                    continue;
                }

                var (start, end) = SemesterTabFormat.MemberRegion(tab);
                var labels = tab.Rows.Skip(start).Take(end - start)
                    .Select(row => row.Count > 0 ? row[0].Trim() : "")
                    .Where(label => label.Length > 0)
                    .GroupBy(label => label, StringComparer.Ordinal)
                    .Where(group => group.Count() == 1)
                    .Select(group => group.Key)
                    .Where(label => !held.Any(m => m.SheetDefinitionId == sheet.Id && m.TabName == tab.Name && m.RowLabel == label))
                    .ToList();
                result.Add((sheet.Id, sheet.Name, tab.Name, labels));
            }
        }

        return result;
    }

    private static List<SheetRowOptionDto> ProjectOptions(List<string> labels, string name) =>
        labels.Select(label => new SheetRowOptionDto(label, NameOf(label) == name))
            .OrderByDescending(o => o.Recommended) // 추천 후보를 최상단으로(나머지는 시트 순서 유지)
            .ToList();

    public async Task SetMappingAsync(int sheetId, string tabName, string userId, string? rowLabel, bool addNewRow = false)
    {
        await currentUser.RequireAdminAsync();
        if (resolver.ResolveByTabName(tabName) is null)
            throw new RuleViolationException("지원하지 않는 학기 탭입니다.");
        rowLabel = string.IsNullOrWhiteSpace(rowLabel) ? null : rowLabel.Trim();
        if (addNewRow && rowLabel is null || rowLabel is { Length: > 150 }
            || rowLabel?.Any(char.IsControl) == true
            || rowLabel is "총 참여자 수" or "탈퇴/퇴출자" or "활동 상태")
            throw new RuleViolationException("부원 행 이름을 1~150자로 입력하세요. 집계/상태 이름과 제어 문자는 사용할 수 없습니다.");

        await ManagedSheetEdits.Gate.WaitAsync();
        try
        {
            await ManagedSheetEdits.RecoverAsync(dbFactory, sheetClient);
            await using var db = await dbFactory.CreateDbContextAsync();
            _ = await db.Users.FirstOrDefaultAsync(u => u.Id == userId)
                ?? throw new NotFoundException("부원을 찾을 수 없습니다.");
            var sheet = await db.SheetDefinitions.FirstOrDefaultAsync(s => s.Id == sheetId && s.Kind == SheetKind.Attendance)
                ?? throw new NotFoundException("출석 시트를 찾을 수 없습니다.");
            var own = await db.SheetMemberMappings.FirstOrDefaultAsync(m =>
                m.SheetDefinitionId == sheetId && m.TabName == tabName && m.UserId == userId);
            if (rowLabel is null)
            {
                if (own is null) return;
                db.SheetMemberMappings.Remove(own);
                await db.SaveChangesAsync();
            }
            else
            {
                var held = await db.SheetMemberMappings.AnyAsync(m => m.SheetDefinitionId == sheetId
                    && m.TabName == tabName && m.RowLabel == rowLabel && m.UserId != userId);
                if (held) throw new ConflictException($"'{rowLabel}'은(는) 이미 다른 계정에 연결되어 있습니다.");
                var before = await sheetClient.GetManagedTabAsync(sheet.SpreadsheetId, tabName);
                var tab = new SheetTab(before.Name, before.Rows);
                var (start, end) = SemesterTabFormat.MemberRegion(tab);
                var count = tab.Rows.Skip(start).Take(end - start).Count(r => r.Count > 0 && r[0].Trim() == rowLabel);
                // A successfully committed request whose HTTP response was lost is a no-op, not another insertion.
                if (own?.RowLabel == rowLabel && count == 1) return;
                if (addNewRow)
                {
                    if (tab.Rows.Any(r => r.Count > 0 && r[0].Trim() == rowLabel))
                        throw new ConflictException("이 이름의 행이 이미 있습니다. 기존 행 연결을 선택하거나 다른 이름을 입력하세요.");
                }
                else if (count != 1)
                    throw new ConflictException("부원 구간에 유일하게 존재하는 행만 연결할 수 있습니다. 새로고침 후 확인하세요.");

                Task SaveMapping()
                {
                    if (own is null) db.SheetMemberMappings.Add(new Domain.Entities.SheetMemberMapping
                        { SheetDefinitionId = sheetId, TabName = tabName, RowLabel = rowLabel, UserId = userId });
                    else own.RowLabel = rowLabel;
                    return Task.CompletedTask;
                }
                if (addNewRow)
                {
                    var mutation = new ManagedSheetMutation(Guid.NewGuid().ToString("N"), SheetMutationKind.InsertMemberRow,
                        0, userId, [ManagedSheetEdits.Json(new { stringValue = rowLabel })], end + 1);
                    MemberRowEdits.Validate(before, mutation);
                    await ManagedSheetEdits.CommitAsync(db, sheetClient, sheet.SpreadsheetId, before, mutation, SaveMapping);
                }
                else
                {
                    await SaveMapping();
                    await db.SaveChangesAsync();
                }
            }
            await auditLog.LogAsync(AdminAction.MemberSheetMapping, "SheetMemberMapping",
                $"{sheetId}/{tabName}/{userId}", rowLabel is null ? "시트 연결 해제" : $"시트 연결: '{rowLabel}'");
        }
        finally { ManagedSheetEdits.Gate.Release(); }
    }

    public async Task<IReadOnlySet<string>> GetUnmappedUserIdsAsync()
    {
        await currentUser.RequireAdminAsync();
        await using var db = await dbFactory.CreateDbContextAsync();

        var tabName = resolver.ResolveByDate(KstClock.Today(time))?.TabName;
        if (tabName is null)
        {
            return new HashSet<string>();
        }

        var sheetIds = await db.SheetDefinitions
            .Where(s => s.Kind == SheetKind.Attendance)
            .Select(s => s.Id)
            .ToListAsync();
        if (sheetIds.Count == 0)
        {
            return new HashSet<string>();
        }

        var mapped = await db.SheetMemberMappings
            .Where(m => m.TabName == tabName && sheetIds.Contains(m.SheetDefinitionId))
            .Select(m => m.UserId)
            .ToHashSetAsync();

        var unmapped = await db.Users.Select(u => u.Id).ToHashSetAsync();
        unmapped.ExceptWith(mapped);
        return unmapped;
    }

    /// <summary>라벨에서 이름 추출 — "박유진 24 만화" → "박유진".</summary>
    private static string NameOf(string rowLabel) =>
        rowLabel.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "";
}
