using VolleyballClub.Application.Abstractions;
using VolleyballClub.Application.Dtos;
namespace VolleyballClub.Client.Services;
public sealed class ActivityClient(ApiHttp api)
{
    public Task<MyTodayDto> GetMyTodayAsync() => api.GetAsync<MyTodayDto>($"api/my/activity");
    public Task<AdminTodayDto> GetOrCreateTodayAsync() => api.PostAsync<AdminTodayDto>($"api/admin/activity/today");
    public Task<AdminTodayDto> GetTodayForAdminAsync() => api.GetAsync<AdminTodayDto>($"api/admin/activity/today");
    public Task<string> GenerateOtpAsync() => api.PostAsync<string>($"api/admin/activity/otp");
    public Task<AdminTodayDto> StartActivityAsync() => api.PostAsync<AdminTodayDto>($"api/admin/activity/start");
    public Task<AdminTodayDto> StartNewActivityAsync() => api.PostAsync<AdminTodayDto>($"api/admin/activity/new");
    public Task CancelActivityAsync() => api.PostAsync($"api/admin/activity/cancel");
    public Task<AdminTodayDto> CloseActivityAsync() => api.PostAsync<AdminTodayDto>($"api/admin/activity/close");
    public Task<AdminTodayDto> ReopenActivityAsync() => api.PostAsync<AdminTodayDto>($"api/admin/activity/reopen");
}
