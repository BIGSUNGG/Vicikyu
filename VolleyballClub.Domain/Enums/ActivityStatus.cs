namespace VolleyballClub.Domain.Enums;

/// <summary>당일 활동의 상태 흐름: AttendanceOpen(활동 중, 출석 상시 오픈) → TeamCreated → ActivityClosed.
/// AttendanceClosed는 과거 출석 마감 기능의 레거시 값 — 신규 할당 없음(현재 DB에는 존재하지 않음).
/// 활동 시작 전에는 DB 행이 없다(null = 가상 '준비 중').</summary>
public enum ActivityStatus
{
    AttendanceOpen = 1,

    /// <summary>레거시: 출석 마감. 더 이상 할당되지 않는다.</summary>
    AttendanceClosed = 2,

    TeamCreated = 3,
    ActivityClosed = 4,
}
