using VolleyballClub.Application.Dtos;

namespace VolleyballClub.Client.Services;

public sealed class AdminActivityClient(ApiHttp api)
{
    public Task<List<ActivityTargetDto>> GetTargetsAsync() => api.GetAsync<List<ActivityTargetDto>>("api/admin/activities/targets");
    public Task<List<ManagedActivityDto>> GetActivitiesAsync() => api.GetAsync<List<ManagedActivityDto>>("api/admin/activities");
    public Task<ManagedActivityDto> CreateAsync(ActivityCreateRequest request) => api.PostAsync<ManagedActivityDto>("api/admin/activities", request);
    public Task<ManagedActivityDto> EditAsync(string key, ActivityEditRequest request) => api.PutAsync<ManagedActivityDto>($"api/admin/activities/{ApiHttp.Escape(key)}", request);
    public Task DeleteAsync(string key) => api.DeleteAsync($"api/admin/activities/{ApiHttp.Escape(key)}");
    public Task<List<AttendanceMemberDto>> GetMembersAsync(string key) => api.GetAsync<List<AttendanceMemberDto>>($"api/admin/activities/{ApiHttp.Escape(key)}/members");
    public Task SetAttendanceAsync(string key, string userId, ActivityAttendanceRequest request) => api.PutAsync($"api/admin/activities/{ApiHttp.Escape(key)}/members/{ApiHttp.Escape(userId)}", request);
    public Task<MemberActivityListDto> GetMemberActivitiesAsync(string userId) => api.GetAsync<MemberActivityListDto>($"api/admin/members/{ApiHttp.Escape(userId)}/activities");
}
