using VolleyballClub.Domain.Enums;

namespace VolleyballClub.Application.Dtos;

public record AttendanceMemberDto(
    string UserId,
    string Name,
    string StudentNumber,
    string Department,
    bool IsAttended,
    DateTime? CheckedAt,
    AttendanceType? AttendanceType,
    int AttendanceId);

public record CalendarDayDto(
    DateOnly Date,
    CalendarDayKind Kind,
    DateTime? CheckedAt);

/// <summary>캘린더 하루 상태 — 출석/미출석(활동은 있었으나 불참)/활동 예정/활동 없음.</summary>
public enum CalendarDayKind
{
    None = 0,
    Attended = 1,
    Missed = 2,
    Scheduled = 3,
    NoActivity = 4,
}

/// <summary>현재 집계 중인 학기 범위 — Name이 null이면 관리자 설정 없이 기본 규칙(3/1~8/31, 9/1~12/31)으로 집계 중.</summary>
public record SemesterScopeDto(
    string? Name,
    DateOnly Start,
    DateOnly End);

public record MyCalendarDto(
    int Year,
    int Month,
    List<CalendarDayDto> Days,
    int MonthCount,
    int SemesterCount,
    int TotalCount,
    SemesterScopeDto? Semester = null);

/// <summary>기록 페이지용 활동 1건 — 날짜별 이벤트(출석·활동 종료)의 원천.</summary>
public record ActivityRecordDto(
    int ActivityId,
    DateOnly Date,
    ActivityStatus Status,
    bool IsAttended,
    DateTime? CheckedAt);

/// <summary>기록 페이지 월간 조회 결과 — 해당 월의 모든 활동(최신순)과 출석 수.</summary>
public record MyRecordsDto(
    int Year,
    int Month,
    List<ActivityRecordDto> Records,
    int MonthCount,
    int MonthActivities);

public record MySemesterStatsDto(
    int SemesterActivities,
    int MyAttendances,
    int AttendanceRatePercent,
    int Rank,
    int TotalActiveMembers,
    SemesterScopeDto? Semester = null);

public record AttendanceRankingDto(
    int Rank,
    string UserId,
    string Name,
    string Department,
    int Count,
    bool IsViewer);
