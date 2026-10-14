using VolleyballClub.Application.Abstractions;
using VolleyballClub.Application.Dtos;
namespace VolleyballClub.Client.Services;
public sealed class AuditLogClient(ApiHttp api)
{
    public Task<List<AuditLogDto>> GetRecentAsync(int take = 100) => api.GetAsync<List<AuditLogDto>>($"api/admin/audit?take={take}");
}
