using VolleyballClub.Application.Dtos;

namespace VolleyballClub.Application.Abstractions;

public interface ITeamService
{
    /// <summary>부원용: 오늘 확정된 최신 회차의 팀 목록(확정된 회차가 없으면 빈 목록).</summary>
    Task<List<TeamResultDto>> GetTodayTeamsAsync();

    /// <summary>관리자용: 상태·확정 무관하게 오늘 최신 회차의 팀 목록.</summary>
    Task<List<TeamResultDto>> GetTodayTeamsForAdminAsync();

    /// <summary>오늘 확정(부원 공개)된 회차 번호. 없으면 null.</summary>
    Task<int?> GetConfirmedDrawNumberAsync();

    Task<ParticipantListDto> GetParticipantsAsync();

    Task<List<TeamResultDto>> GenerateAsync(TeamCreateRequest request);

    Task<TeamResultDto> MoveMemberAsync(int teamMemberId, int targetTeamId);

    Task ConfirmAsync();

    Task AddGuestAsync(string name);

    Task RemoveGuestAsync(int guestId);
}
