using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;
using VolleyballClub.Application.Abstractions;
using VolleyballClub.Application.Exceptions;
using VolleyballClub.Domain.Entities;
using VolleyballClub.Domain.Enums;
using VolleyballClub.Infrastructure.Persistence;

namespace VolleyballClub.Infrastructure.Services;

/// <summary>관리자 시트(스프레드시트) 등록·수정·삭제. 현재 종류는 출석부(Attendance) 단일.</summary>
public partial class SheetAdminService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ICurrentUserService currentUser,
    IAuditLogService auditLog) : ISheetAdminService
{
    [GeneratedRegex(@"\/spreadsheets\/d\/([A-Za-z0-9_-]+)")]
    private static partial Regex SpreadsheetIdRegex();

    public async Task<List<SheetDefinitionDto>> GetListAsync()
    {
        await currentUser.RequireAdminAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.SheetDefinitions
            .OrderBy(s => s.Id)
            .Select(s => new SheetDefinitionDto(s.Id, s.Name, s.SpreadsheetId, s.Kind.ToString()))
            .ToListAsync();
    }

    public async Task<int> SaveAsync(int? id, string name, string spreadsheetIdOrUrl)
    {
        await currentUser.RequireAdminAsync();
        name = name.Trim();
        var spreadsheetId = ExtractSpreadsheetId(spreadsheetIdOrUrl);
        if (name.Length == 0)
        {
            throw new ConflictException("시트 이름을 입력하세요.");
        }

        if (spreadsheetId.Length == 0)
        {
            throw new ConflictException("스프레드시트 주소(ID)를 입력하세요.");
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.SheetDefinitions.AnyAsync(s => s.Name == name && s.Id != id))
        {
            throw new ConflictException($"시트 이름 '{name}'이(가) 이미 있습니다.");
        }

        var now = DateTime.UtcNow;
        SheetDefinition sheet;
        if (id is null)
        {
            sheet = new SheetDefinition
            {
                Name = name,
                SpreadsheetId = spreadsheetId,
                Kind = SheetKind.Attendance,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.SheetDefinitions.Add(sheet);
        }
        else
        {
            sheet = await db.SheetDefinitions.FirstOrDefaultAsync(s => s.Id == id)
                ?? throw new NotFoundException("시트를 찾을 수 없습니다.");
            sheet.Name = name;
            sheet.SpreadsheetId = spreadsheetId;
            sheet.UpdatedAt = now;
        }

        await db.SaveChangesAsync();
        await auditLog.LogAsync(AdminAction.SettingChange, "SheetDefinition", sheet.Id.ToString(),
            $"시트 '{sheet.Name}' {(id is null ? "등록" : "수정")}");
        return sheet.Id;
    }

    public async Task DeleteAsync(int id)
    {
        await currentUser.RequireAdminAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var sheet = await db.SheetDefinitions.FirstOrDefaultAsync(s => s.Id == id)
            ?? throw new NotFoundException("시트를 찾을 수 없습니다.");
        if (await db.ActivitySources.AnyAsync(s => s.SheetDefinitionId == id))
            throw new ConflictException("연결된 활동 원본이 있어 시트를 삭제할 수 없습니다. 활동 원본을 먼저 정리하세요.");
        db.SheetDefinitions.Remove(sheet); // 부원 매핑은 FK cascade
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException)
        {
            if (await db.ActivitySources.AsNoTracking().AnyAsync(s => s.SheetDefinitionId == id))
                throw new ConflictException("연결된 활동 원본이 있어 시트를 삭제할 수 없습니다.");
            throw;
        }
        await auditLog.LogAsync(AdminAction.SettingChange, "SheetDefinition", id.ToString(), $"시트 '{sheet.Name}' 삭제");
    }

    /// <summary>전체 URL이면 /d/{id}에서 ID를 추출하고, 아니면 입력값을 그대로 ID로 쓴다.</summary>
    private static string ExtractSpreadsheetId(string input)
    {
        input = input.Trim();
        if (input.Length == 0)
        {
            return "";
        }

        var match = SpreadsheetIdRegex().Match(input);
        return match.Success ? match.Groups[1].Value : input;
    }
}
