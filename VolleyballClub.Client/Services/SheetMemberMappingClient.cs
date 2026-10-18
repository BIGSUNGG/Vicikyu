using VolleyballClub.Application.Abstractions;
using VolleyballClub.Application.Dtos;
namespace VolleyballClub.Client.Services;
public sealed class SheetMemberMappingClient(ApiHttp api)
{
    public Task<List<MemberTabMappingDto>> GetForMemberAsync(string userId) => api.GetAsync<List<MemberTabMappingDto>>($"api/admin/members/{ApiHttp.Escape(userId)}/mappings");
    public Task SetMappingAsync(int sheetId, string tabName, string userId, string? rowLabel, bool addNewRow = false) => api.PutAsync($"api/admin/members/{ApiHttp.Escape(userId)}/mappings", new SheetMappingRequest(sheetId, tabName, rowLabel, addNewRow));
}
