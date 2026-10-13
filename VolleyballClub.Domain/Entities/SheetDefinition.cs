using VolleyballClub.Domain.Enums;

namespace VolleyballClub.Domain.Entities;

/// <summary>관리자가 등록한 구글 시트(스프레드시트). Kind가 Attendance인 것만 출석 동기화 대상이다.</summary>
public class SheetDefinition
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    /// <summary>스프레드시트 ID(또는 URL에서 추출한 ID).</summary>
    public string SpreadsheetId { get; set; } = "";

    public SheetKind Kind { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
