using VolleyballClub.Application.Dtos;

namespace VolleyballClub.Application.Abstractions;

public interface IActivityService
{
    Task<AdminTodayDto> GetTodayForAdminAsync();

    /// <summary>부원용 오늘 활동. 시작 전 예정 표시면 가상 '활동 예정', 일정이 없으면 null.</summary>
    Task<MyTodayDto?> GetMyTodayAsync(string userId);

    Task<string> GenerateOtpAsync();

    /// <summary>오늘 활동을 즉시 시작한다(AttendanceOpen 생성, OTP 자동 생성). 마감된 활동이 있으면 남긴 채 새 세션으로 시작.</summary>
    Task<AdminTodayDto> StartNewActivityAsync();

    Task CancelActivityAsync();

    Task<AdminTodayDto> CloseActivityAsync();

    Task<AdminTodayDto> ReopenActivityAsync();
}
