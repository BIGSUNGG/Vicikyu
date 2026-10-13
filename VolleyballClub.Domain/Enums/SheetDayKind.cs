namespace VolleyballClub.Domain.Enums;

/// <summary>시트 날짜 열의 활동 상태(2026-2 신형 서식 — 1행 Enum).
/// Completed는 실제 Activity로 가져오므로 SheetDayMark에는 Scheduled/NoActivity만 저장된다.</summary>
public enum SheetDayKind
{
    Completed = 0,
    Scheduled = 1,
    NoActivity = 2,
}
