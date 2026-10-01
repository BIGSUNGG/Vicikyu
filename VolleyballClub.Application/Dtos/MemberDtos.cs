namespace VolleyballClub.Application.Dtos;

public record MemberListDto(
    string UserId,
    string Name,
    string StudentNumber,
    string Department,
    string Role,
    bool IsActive,
    int TotalAttendance,
    bool FeePaidCurrent);

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
    bool? IsActive = null);

public record ProfileDto(
    string Name,
    string StudentNumber,
    string Department,
    string Role,
    DateTime JoinedAt,
    int TotalAttendance);
