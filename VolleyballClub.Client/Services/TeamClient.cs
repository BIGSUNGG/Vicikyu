using VolleyballClub.Application.Abstractions;
using VolleyballClub.Application.Dtos;
namespace VolleyballClub.Client.Services;
public sealed class TeamClient(ApiHttp api)
{
    public Task<List<TeamResultDto>> GetTodayTeamsAsync() => api.GetAsync<List<TeamResultDto>>($"api/my/teams");
    public Task<List<TeamResultDto>> GetTodayTeamsForAdminAsync() => api.GetAsync<List<TeamResultDto>>($"api/admin/teams");
    public Task<ParticipantListDto> GetParticipantsAsync() => api.GetAsync<ParticipantListDto>($"api/admin/teams/participants");
    public Task<List<TeamResultDto>> GenerateAsync(TeamCreateRequest request) => api.PostAsync<List<TeamResultDto>>($"api/admin/teams/generate", request);
    public Task<TeamResultDto> MoveMemberAsync(int teamMemberId, int targetTeamId) => api.PutAsync<TeamResultDto>($"api/admin/teams/members/{teamMemberId}", new TeamMoveRequest(targetTeamId));
    public Task RemoveMemberAsync(int teamMemberId) => api.DeleteAsync($"api/admin/teams/members/{teamMemberId}");
    public Task<TeamResultDto> AddMemberAsync(string participantKey, int teamId) => api.PostAsync<TeamResultDto>($"api/admin/teams/members", new TeamAddRequest(participantKey, teamId));
    public Task CancelTeamsAsync() => api.PostAsync($"api/admin/teams/cancel");
    public Task ConfirmAsync() => api.PostAsync($"api/admin/teams/confirm");
    public Task AddGuestAsync(string name) => api.PostAsync($"api/admin/teams/guests", new TextRequest(name));
    public Task RemoveGuestAsync(int guestId) => api.DeleteAsync($"api/admin/teams/guests/{guestId}");
}
