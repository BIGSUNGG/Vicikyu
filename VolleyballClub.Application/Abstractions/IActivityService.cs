using VolleyballClub.Application.Dtos;

namespace VolleyballClub.Application.Abstractions;

public interface IActivityService
{
    Task<AdminTodayDto> GetOrCreateTodayAsync();

    Task<AdminTodayDto> GetTodayForAdminAsync();

    Task<MyTodayDto> GetMyTodayAsync(string userId);

    Task<string> GenerateOtpAsync();

    Task<AdminTodayDto> StartActivityAsync();

    /// <summary>마감된 활동을 남긴 채 오늘 날짜 새 세션을 즉시 시작한다. 이전 세션 출석과 별도로 기록된다.</summary>
    Task<AdminTodayDto> StartNewActivityAsync();

    Task CancelActivityAsync();

    Task<AdminTodayDto> CloseActivityAsync();

    Task<AdminTodayDto> ReopenActivityAsync();
}
