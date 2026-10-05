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

    /// <summary>최신 회차에서 팀원을 제거한다(이전 회차는 불가).</summary>
    Task RemoveMemberAsync(int teamMemberId);

    /// <summary>편성 후 누락된 부원/게스트를 최신 회차의 특정 팀에 배정한다.</summary>
    Task<TeamResultDto> AddMemberAsync(string participantKey, int teamId);

    /// <summary>최신 회차를 취소한다. 취소된 회차가 확정 상태였으면 공개를 이전 회차로 되돌린다.</summary>
    Task CancelLatestDrawAsync();

    Task ConfirmAsync();

    Task AddGuestAsync(string name);

    Task RemoveGuestAsync(int guestId);
}
