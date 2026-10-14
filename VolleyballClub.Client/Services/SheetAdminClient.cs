using VolleyballClub.Application.Abstractions;
using VolleyballClub.Application.Dtos;
namespace VolleyballClub.Client.Services;
public sealed class SheetAdminClient(ApiHttp api)
{
    public Task<List<SheetDefinitionDto>> GetListAsync() => api.GetAsync<List<SheetDefinitionDto>>($"api/admin/sheets");
    public Task<int> SaveAsync(int? id, string name, string spreadsheetIdOrUrl) => api.PostAsync<int>($"api/admin/sheets", new SheetSaveRequest(id, name, spreadsheetIdOrUrl));
    public Task DeleteAsync(int id) => api.DeleteAsync($"api/admin/sheets/{id}");
}
