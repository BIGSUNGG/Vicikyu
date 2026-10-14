using VolleyballClub.Application.Abstractions;
using VolleyballClub.Application.Dtos;
namespace VolleyballClub.Client.Services;
public sealed class FeeClient(ApiHttp api)
{
    public Task<List<FeePeriodDto>> GetPeriodsAsync() => api.GetAsync<List<FeePeriodDto>>($"api/admin/fees");
    public Task<FeePeriodDto?> GetCurrentPeriodAsync() => api.GetNullableAsync<FeePeriodDto>($"api/my/fees/current");
    public Task<List<FeeStatusDto>> GetMyStatusesAsync() => api.GetAsync<List<FeeStatusDto>>($"api/my/fees");
    public Task<FeePeriodDto> CreatePeriodAsync(FeePeriodCreateRequest request) => api.PostAsync<FeePeriodDto>($"api/admin/fees", request);
    public Task<FeePeriodDto> UpdatePeriodAsync(int periodId, FeePeriodUpdateRequest request) => api.PutAsync<FeePeriodDto>($"api/admin/fees/{periodId}", request);
    public Task DeletePeriodAsync(int periodId) => api.DeleteAsync($"api/admin/fees/{periodId}");
    public Task<List<FeeStatusDto>> GetPeriodStatusesAsync(int periodId, FeeFilter filter = FeeFilter.All, string? search = null) => api.GetAsync<List<FeeStatusDto>>($"api/admin/fees/{periodId}/statuses?filter={(int)filter}&search={ApiHttp.Escape(search)}");
    public Task SetPaidAsync(int periodId, string userId, bool isPaid, string? memo = null) => api.PutAsync($"api/admin/fees/{periodId}/members/{ApiHttp.Escape(userId)}", new FeePaymentRequest(isPaid, memo));
}
