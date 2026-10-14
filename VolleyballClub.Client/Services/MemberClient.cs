using VolleyballClub.Application.Abstractions;
using VolleyballClub.Application.Dtos;
namespace VolleyballClub.Client.Services;
public sealed class MemberClient(ApiHttp api)
{
    public Task<MemberPageDto> GetPageAsync(MemberPageRequest request) => api.PostAsync<MemberPageDto>($"api/admin/members/search", request);
    public Task<MemberListDto> GetAsync(string userId) => api.GetAsync<MemberListDto>($"api/admin/members/{ApiHttp.Escape(userId)}");
    public Task<MemberListDto> AddAsync(MemberCreateRequest request) => api.PostAsync<MemberListDto>($"api/admin/members", request);
    public Task<MemberListDto> UpdateAsync(string userId, MemberUpdateRequest request) => api.PutAsync<MemberListDto>($"api/admin/members/{ApiHttp.Escape(userId)}", request);
    public Task<ProfileDto> GetProfileAsync() => api.GetAsync<ProfileDto>($"api/my/profile");
    public Task UpdateMyColorAsync(string color) => api.PutAsync($"api/my/profile/color", new TextRequest(color));
    public async Task ChangeMyPasswordAsync(PasswordChangeRequest request)
    {
        await api.PutAsync("api/my/profile/password", request);
        // Identity invalidates the old security stamp; do not retain a logged-in browser display.
        await api.RefreshIdentityAsync();
    }
}
