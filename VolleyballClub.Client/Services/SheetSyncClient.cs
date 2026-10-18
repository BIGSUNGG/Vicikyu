using VolleyballClub.Application.Abstractions;
using VolleyballClub.Application.Dtos;
namespace VolleyballClub.Client.Services;
public sealed class SheetSyncClient(ApiHttp api)
{
    public Task<SheetSyncReport> ResyncAsync() => api.PostAsync<SheetSyncReport>("api/admin/sheets/resync");
    public Task<SheetSyncStatusDto> GetStatusAsync() => api.GetAsync<SheetSyncStatusDto>("api/admin/sheets/sync-status");
}
