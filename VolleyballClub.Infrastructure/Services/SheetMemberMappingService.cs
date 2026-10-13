using Microsoft.EntityFrameworkCore;
using VolleyballClub.Application.Abstractions;
using VolleyballClub.Application.Exceptions;
using VolleyballClub.Domain;
using VolleyballClub.Domain.Enums;
using VolleyballClub.Infrastructure.Identity;
using VolleyballClub.Infrastructure.Persistence;

namespace VolleyballClub.Infrastructure.Services;

/// <summary>탭별 부원↔시트 행 연결 관리. 행 후보는 시트를 실시간으로 읽고
/// 라벨의 이름 접두사가 부원 이름과 같으면 추천(최상단 강조)으로 올린다.
/// 연결 변경 즉시 동기화를 실행해 과거 출석이 바로 반영된다(재기동 불필요).</summary>
public sealed class SheetMemberMappingService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ISheetClient sheetClient,
    ISheetFormatResolver resolver,
    ICurrentUserService currentUser,
    IAuditLogService auditLog,
    ISheetSyncService sync,
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

        var sheets = await db.SheetDefinitions
            .Where(s => s.Kind == SheetKind.Attendance)
            .OrderBy(s => s.Id)
            .ToListAsync();
        var mappings = await db.SheetMemberMappings
            .Where(m => m.UserId == userId)
            .ToListAsync();

        var result = new List<MemberTabMappingDto>();
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

                var labels = tab.Rows
                    .Skip(1)
                    .Select(row => row.Count > 0 ? row[0].Trim() : "")
                    .Where(label => label.Length > 0)
                    .Distinct()
                    .ToList();
                var options = labels
                    .Select(label => new SheetRowOptionDto(label, NameOf(label) == memberName))
                    .OrderByDescending(o => o.Recommended) // 추천 후보를 최상단으로(나머지는 시트 순서 유지)
                    .ToList();

                result.Add(new MemberTabMappingDto(
                    sheet.Id,
                    sheet.Name,
                    tab.Name,
                    mappings.FirstOrDefault(m => m.SheetDefinitionId == sheet.Id && m.TabName == tab.Name)?.RowLabel,
                    options));
            }
        }

        return result;
    }

    public async Task<SheetSyncReport?> SetMappingAsync(int sheetId, string tabName, string userId, string? rowLabel)
    {
        await currentUser.RequireAdminAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        _ = await db.Users.FirstOrDefaultAsync(u => u.Id == userId)
            ?? throw new NotFoundException("부원을 찾을 수 없습니다.");
        _ = await db.SheetDefinitions.FirstOrDefaultAsync(s => s.Id == sheetId)
            ?? throw new NotFoundException("시트를 찾을 수 없습니다.");

        var own = await db.SheetMemberMappings
            .FirstOrDefaultAsync(m => m.SheetDefinitionId == sheetId && m.TabName == tabName && m.UserId == userId);
        rowLabel = string.IsNullOrWhiteSpace(rowLabel) ? null : rowLabel.Trim();

        if (rowLabel is null)
        {
            if (own is not null)
            {
                db.SheetMemberMappings.Remove(own);
                await db.SaveChangesAsync();
                await auditLog.LogAsync(AdminAction.MemberSheetMapping, "SheetMemberMapping",
                    $"{sheetId}/{tabName}/{userId}", "시트 연결 해제");

                return await TrySyncAsync();
            }

            return null;
        }

        var held = await db.SheetMemberMappings.FirstOrDefaultAsync(m =>
            m.SheetDefinitionId == sheetId && m.TabName == tabName && m.RowLabel == rowLabel && m.UserId != userId);
        if (held is not null)
        {
            var holderName = await db.Users.Where(u => u.Id == held.UserId).Select(u => u.Name).FirstOrDefaultAsync();
            throw new ConflictException($"'{rowLabel}'은(는) 이미 {holderName}님에게 연결되어 있습니다.");
        }

        if (own is null)
        {
            db.SheetMemberMappings.Add(new Domain.Entities.SheetMemberMapping
            {
                SheetDefinitionId = sheetId,
                TabName = tabName,
                RowLabel = rowLabel,
                UserId = userId,
            });
        }
        else
        {
            own.RowLabel = rowLabel;
        }

        await db.SaveChangesAsync();
        await auditLog.LogAsync(AdminAction.MemberSheetMapping, "SheetMemberMapping",
            $"{sheetId}/{tabName}/{userId}", $"시트 연결: '{rowLabel}'");

        // 매핑 변경 즉시 반영 — 재기동 없이 과거 출석이 백필된다
        return await TrySyncAsync();
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

    /// <summary>동기화 실패는 매핑 저장을 롤백하지 않는다 — null 반환(다음 기동 SyncAsync가 반영).</summary>
    private async Task<SheetSyncReport?> TrySyncAsync()
    {
        try
        {
            return await sync.SyncAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>라벨에서 이름 추출 — "박유진 24 만화" → "박유진".</summary>
    private static string NameOf(string rowLabel) =>
        rowLabel.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "";
}
