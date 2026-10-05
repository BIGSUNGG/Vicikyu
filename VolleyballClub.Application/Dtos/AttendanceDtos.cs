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
    bool IsAttended,
    DateTime? CheckedAt,
    string? TeamName);

public record MyCalendarDto(
    int Year,
    int Month,
    List<CalendarDayDto> Days,
    int MonthCount,
    int SemesterCount,
    int TotalCount);

/// <summary>기록 페이지용 활동 1건 — 날짜별 이벤트(출석·팀 배정·활동 종료)의 원천.</summary>
public record ActivityRecordDto(
    DateOnly Date,
    ActivityStatus Status,
    bool IsAttended,
    DateTime? CheckedAt,
    string? TeamName);

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
    int TotalActiveMembers);

public record AttendanceRankingDto(
    int Rank,
    string UserId,
    string Name,
    string Department,
    int Count,
    bool IsViewer);
