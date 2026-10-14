using VolleyballClub.Application.Abstractions;
using VolleyballClub.Application.Dtos;
namespace VolleyballClub.Client.Services;
public sealed class AttendanceClient(ApiHttp api)
{
    public Task<DateTime> CheckInWithOtpAsync(string otp) => api.PostAsync<DateTime>($"api/my/attendance", new TextRequest(otp));
    public Task<List<AttendanceMemberDto>> GetTodayMembersAsync(string? search = null) => api.GetAsync<List<AttendanceMemberDto>>($"api/admin/attendance?search={ApiHttp.Escape(search)}");
    public Task AdminAddAsync(string userId) => api.PostAsync($"api/admin/attendance/{ApiHttp.Escape(userId)}");
    public Task AdminCancelAsync(string userId, bool confirmTeamImpact = false) => api.DeleteAsync($"api/admin/attendance/{ApiHttp.Escape(userId)}", new AttendanceChangeRequest(confirmTeamImpact));
    public Task AdminAddForActivityAsync(string userId, int activityId) => api.PostAsync($"api/admin/members/{ApiHttp.Escape(userId)}/attendance/{activityId}");
    public Task AdminRemoveForActivityAsync(string userId, int activityId, bool confirmTeamImpact = false) => api.DeleteAsync($"api/admin/members/{ApiHttp.Escape(userId)}/attendance/{activityId}", new AttendanceChangeRequest(confirmTeamImpact));
    public Task<MemberAttendanceAdminDto> GetAdminRecordsAsync(string userId) => api.GetAsync<MemberAttendanceAdminDto>($"api/admin/members/{ApiHttp.Escape(userId)}/attendance");
    public Task<MyCalendarDto> GetMyCalendarAsync(int year, int month) => api.GetAsync<MyCalendarDto>($"api/my/calendar?year={year}&month={month}");
    public Task<MyRecordsDto> GetMyRecordsAsync(int year, int month) => api.GetAsync<MyRecordsDto>($"api/my/records?year={year}&month={month}");
    public Task<MySemesterStatsDto> GetMySemesterStatsAsync() => api.GetAsync<MySemesterStatsDto>($"api/my/stats");
}
