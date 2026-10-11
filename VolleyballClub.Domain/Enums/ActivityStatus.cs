namespace VolleyballClub.Domain.Enums;

/// <summary>당일 활동의 상태 흐름: Ready → AttendanceOpen(활동 중, 출석 상시 오픈) → TeamCreated → ActivityClosed.
/// AttendanceClosed는 과거 출석 마감 기능의 레거시 값 — 신규 할당 없음(마이그레이션으로 1/3으로 변환됨).</summary>
public enum ActivityStatus
{
    Ready = 0,
    AttendanceOpen = 1,

    /// <summary>레거시: 출석 마감. 더 이상 할당되지 않는다.</summary>
    AttendanceClosed = 2,

    TeamCreated = 3,
    ActivityClosed = 4,
}
