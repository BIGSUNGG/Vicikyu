using VolleyballClub.Application.Abstractions;
using VolleyballClub.Application.Dtos;
namespace VolleyballClub.Client.Services;
public sealed class SettingClient(ApiHttp api)
{
    public Task SaveGoogleKeyAsync(string value) => api.PutAsync("api/admin/settings/google-key", new TextRequest(value));
}
