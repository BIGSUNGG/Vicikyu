using VolleyballClub.Domain.Enums;

namespace VolleyballClub.Application.Dtos;

public record MemberListDto(
    string UserId,
    string Name,
    string StudentNumber,
    string Department,
    string Role,
    MemberStatus Status,
    int TotalAttendance,
    bool FeePaidCurrent,
    string? Color);

/// <summary>부원 목록 정렬 기준 — 출석 수 3종(학번·이름·학과 정렬은 UI에서 제거됨).</summary>
public enum MemberSortField
{
    TotalAttendance,
    SemesterAttendance,
    MonthAttendance,
}

/// <summary>부원 목록 조회 요청 — 서버 쿼리 필터링·정렬·페이징. 기본 정렬은 이번 학기 출석 내림차순.</summary>
public record MemberPageRequest(
    string? Search = null,
    IReadOnlyCollection<MemberStatus>? Statuses = null,
    bool? SheetMapped = null,
    bool? FeePaid = null,
    MemberSortField Sort = MemberSortField.SemesterAttendance,
    bool Ascending = false,
    int Page = 1,
    int PageSize = 10);

public record MemberListItemDto(
    string UserId,
    string Name,
    string StudentNumber,
    string Department,
    string Role,
    MemberStatus Status,
    string? Color,
    int SemesterAttendance,
    int MonthAttendance,
    int TotalAttendance,
    bool FeePaidCurrent,
    bool SheetMapped);

public record MemberPageDto(
    IReadOnlyList<MemberListItemDto> Items,
    int TotalCount,
    int Page,
    int PageSize);

public record MemberCreateRequest(
    string StudentNumber,
    string Name,
    string Department,
    string Password,
    string Role = "MEMBER");

public record MemberUpdateRequest(
    string Name,
    string Department,
    string? StudentNumber = null,
    string? Role = null,
    MemberStatus? Status = null,
    string? Color = null,
    string? NewPassword = null);

public record PasswordChangeRequest(
    string CurrentPassword,
    string NewPassword);

public record ProfileDto(
    string Name,
    string StudentNumber,
    string Department,
    string Role,
    DateTime JoinedAt,
    int TotalAttendance,
    string? Color);
