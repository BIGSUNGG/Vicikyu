using VolleyballClub.Application.Dtos;

namespace VolleyballClub.Application.Abstractions;

public interface ITeamService
{
    /// <summary>부원용: 오늘 확정된 팀 목록(확정 전이면 빈 목록).</summary>
    Task<List<TeamResultDto>> GetTodayTeamsAsync();

    /// <summary>관리자용: 상태·확정 무관하게 오늘 팀 목록.</summary>
    Task<List<TeamResultDto>> GetTodayTeamsForAdminAsync();

    Task<ParticipantListDto> GetParticipantsAsync();

    /// <summary>현재 팀을 지우고 새로 편성한다(회차·이력 없음).</summary>
    Task<List<TeamResultDto>> GenerateAsync(TeamCreateRequest request);

    Task<TeamResultDto> MoveMemberAsync(int teamMemberId, int targetTeamId);

    Task RemoveMemberAsync(int teamMemberId);

    /// <summary>편성 후 누락된 부원/게스트를 특정 팀에 배정한다.</summary>
    Task<TeamResultDto> AddMemberAsync(string participantKey, int teamId);

    /// <summary>현재 팀 편성을 전체 취소한다(팀 삭제, 상태는 활동 중으로 복귀).</summary>
    Task CancelTeamsAsync();

    /// <summary>현재 팀을 부원에게 공개한다(상태를 팀 편성 완료로).</summary>
    Task ConfirmAsync();

    Task AddGuestAsync(string name);

    Task RemoveGuestAsync(int guestId);
}
