using VolleyballClub.Application.Dtos;

namespace VolleyballClub.Application.Abstractions;

public interface ITeamService
{
    /// <summary>부원용: 오늘 확정된 팀 목록(확정 전이면 빈 목록).</summary>
    Task<List<TeamResultDto>> GetTodayTeamsAsync();

    /// <summary>관리자용: 상태와 무관하게 오늘 팀 목록.</summary>
    Task<List<TeamResultDto>> GetTodayTeamsForAdminAsync();

    Task<ParticipantListDto> GetParticipantsAsync();

    Task<List<TeamResultDto>> GenerateAsync(TeamCreateRequest request);

    Task<TeamResultDto> MoveMemberAsync(int teamMemberId, int targetTeamId);

    Task ConfirmAsync();

    Task AddGuestAsync(string name);

    Task RemoveGuestAsync(int guestId);
}
