namespace VolleyballClub.Domain.Enums;

/// <summary>당일 활동의 상태 흐름: Ready → AttendanceOpen → AttendanceClosed → TeamCreated → ActivityClosed</summary>
public enum ActivityStatus
{
    Ready = 0,
    AttendanceOpen = 1,
    AttendanceClosed = 2,
    TeamCreated = 3,
    ActivityClosed = 4,
}
