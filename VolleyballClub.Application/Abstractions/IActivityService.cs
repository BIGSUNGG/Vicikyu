using VolleyballClub.Application.Dtos;

namespace VolleyballClub.Application.Abstractions;

public interface IActivityService
{
    Task<AdminTodayDto> GetOrCreateTodayAsync();

    Task<AdminTodayDto> GetTodayForAdminAsync();

    Task<MyTodayDto> GetMyTodayAsync(string userId);

    Task<string> GenerateOtpAsync();

    Task<AdminTodayDto> StartActivityAsync();

    Task CancelActivityAsync();

    Task<AdminTodayDto> CloseActivityAsync();

    Task<AdminTodayDto> ReopenActivityAsync();
}
