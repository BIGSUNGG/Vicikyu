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
