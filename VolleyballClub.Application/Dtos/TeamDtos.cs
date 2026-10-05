namespace VolleyballClub.Application.Dtos;

public record ParticipantDto(
    string Key,
    string? UserId,
    int? GuestId,
    string Name,
    string Department,
    bool IsGuest,
    bool IsAttended);

public record ParticipantListDto(
    List<ParticipantDto> Participants,
    int AttendedCount);

public record TeamMemberDto(
    int TeamMemberId,
    string? UserId,
    int? GuestId,
    string Name,
    bool IsGuest);

public record TeamResultDto(
    int TeamId,
    string Name,
    int Order,
    int DrawNumber,
    List<TeamMemberDto> Members);

public record TeamCreateRequest(
    List<string> UserIds,
    List<int> GuestIds,
    List<int> TeamSizes);
