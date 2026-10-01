using VolleyballClub.Domain.Enums;

namespace VolleyballClub.Application.Dtos;

/// <summary>관리자 대시보드용 오늘 활동 요약. OTP 코드는 관리자 전용으로만 노출된다.</summary>
public record AdminTodayDto(
    int ActivityId,
    DateOnly ActivityDate,
    ActivityStatus Status,
    string StatusText,
    string? OtpCode,
    int AttendanceCount,
    int TotalActiveMembers,
    int TeamCount,
    DateTime? AttendanceOpenedAt,
    DateTime? AttendanceClosedAt,
    DateTime? ActivityClosedAt);

/// <summary>부원 홈용 오늘 활동 요약. OTP 값은 포함되지 않는다.</summary>
public record MyTodayDto(
    int ActivityId,
    DateOnly ActivityDate,
    ActivityStatus Status,
    string StatusText,
    bool IsAttended,
    DateTime? CheckedAt,
    AttendanceType? AttendanceType,
    int AttendanceCount,
    int TotalActiveMembers);
