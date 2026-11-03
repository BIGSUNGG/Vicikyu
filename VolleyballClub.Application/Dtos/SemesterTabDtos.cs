using VolleyballClub.Domain.Enums;

namespace VolleyballClub.Application.Dtos;

/// <summary>xlsx 탭 하나의 원시 그리드. Rows[r][c]는 셀 값을 그대로 담고, 뒤쪽 빈 셀은 잘려 들어올 수 있다.</summary>
public sealed record TabGrid(string Name, IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>학기 탭 파싱 결과 — 활동 날짜마다 출석한 행 라벨 목록.</summary>
public sealed record SemesterTabData(IReadOnlyList<ActivityColumnData> Activities);

/// <summary>탭 한 열(=활동 1회)의 정보. Kind가 Completed인 열만 출석을 담고
/// 예정/없음 열은 AttendedRowLabels가 비어 있다(활동 불가·미출석은 미출석으로 흡수해 기록 없음).
/// Label은 날짜 헤더 뒤에 붙은 라벨 활동 이름(예: '26. 09. 04. 금 OT'의 'OT').</summary>
public sealed record ActivityColumnData(
    DateOnly Date,
    IReadOnlyList<string> AttendedRowLabels,
    DayKind Kind = DayKind.Completed,
    string? Label = null,
    string? Reason = null);
