namespace VolleyballClub.Application.Dtos;

/// <summary>시트 탭 하나의 원시 데이터. Rows[r][c]는 셀 값을 그대로 담고,
/// 뒤쪽 빈 셀·빈 행은 Values API 특성상 잘려 들어오지 않는다(전략이 빈 칸으로 처리).</summary>
public sealed record SheetTab(string Name, IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>탭에 기록할 열 하나. FirstRowIndex(1-based)부터 Cells를 세로로 쓴다.</summary>
public sealed record SheetColumn(int FirstRowIndex, IReadOnlyList<string> Cells);

/// <summary>열 기록 대상 — InsertColumn이면 columnIndex 위치에 새 열을 삽입한 뒤 기록,
/// 아니면 columnIndex 기존 열에 덮어쓴다.</summary>
public sealed record SheetWriteTarget(bool InsertColumn, int ColumnIndex, SheetColumn Column);

/// <summary>학기 탭 파싱 결과 — 활동 날짜마다 출석한 행 라벨 목록.</summary>
public sealed record SheetSemesterData(IReadOnlyList<SheetActivityColumn> Activities);

/// <summary>시트 한 열(=활동 1회)의 출석 정보. 불참/공백은 아예 담지 않는다.</summary>
public sealed record SheetActivityColumn(DateOnly Date, IReadOnlyList<string> AttendedRowLabels);
