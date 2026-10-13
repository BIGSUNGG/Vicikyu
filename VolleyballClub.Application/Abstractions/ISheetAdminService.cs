namespace VolleyballClub.Application.Abstractions;

/// <summary>관리자가 등록한 시트(스프레드시트) 목록.</summary>
public sealed record SheetDefinitionDto(int Id, string Name, string SpreadsheetId, string Kind);

public interface ISheetAdminService
{
    Task<List<SheetDefinitionDto>> GetListAsync();

    /// <summary>시트 등록/수정. spreadsheetIdOrUrl에는 ID 또는 전체 URL을 넣을 수 있다(서비스가 ID를 추출).</summary>
    Task<int> SaveAsync(int? id, string name, string spreadsheetIdOrUrl);

    Task DeleteAsync(int id);
}
