using VolleyballClub.Application.Dtos;

namespace VolleyballClub.Application.Abstractions;

/// <summary>구글 시트 접근 계약. 원시 탭 읽기·열 기록/삽입만 제공하고,
/// 서식 해석(어느 셀이 날짜·부원·출석 표기인지)은 학기별 전략이 담당한다.</summary>
public interface ISheetClient
{
    /// <summary>스프레드시트의 탭들 원시 셀 값을 읽는다. tabNames가 null이면 전체 탭.</summary>
    Task<IReadOnlyList<SheetTab>> GetTabsAsync(string spreadsheetId, IEnumerable<string>? tabNames = null, CancellationToken ct = default);

    /// <summary>columnIndex(1-based) 열에 FirstRowIndex부터 Cells를 세로로 기록한다(기존 값 덮어씀).</summary>
    Task WriteColumnAsync(string spreadsheetId, string tabName, int columnIndex, SheetColumn column, CancellationToken ct = default);

    /// <summary>columnIndex 위치에 새 열을 삽입(왼쪽 열 서식 상속)하고 Cells를 기록한다.</summary>
    Task InsertColumnAsync(string spreadsheetId, string tabName, int columnIndex, SheetColumn column, CancellationToken ct = default);
}
