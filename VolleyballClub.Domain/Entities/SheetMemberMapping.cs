namespace VolleyballClub.Domain.Entities;

/// <summary>탭(학기)별 부원↔시트 행 연결. 레코드가 없으면 미지정(동기화에서 건너뛴다).
/// (SheetDefinitionId, TabName, RowLabel)와 (SheetDefinitionId, TabName, UserId) 각각 유일.</summary>
public class SheetMemberMapping
{
    public int Id { get; set; }

    public int SheetDefinitionId { get; set; }

    public SheetDefinition SheetDefinition { get; set; } = null!;

    /// <summary>학기 탭 이름(예: "2026-2").</summary>
    public string TabName { get; set; } = "";

    /// <summary>시트 행의 라벨(예: "박유진 24 만화").</summary>
    public string RowLabel { get; set; } = "";

    /// <summary>연결된 앱 부원(UserId).</summary>
    public string UserId { get; set; } = "";
}
