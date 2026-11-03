using VolleyballClub.Application.Dtos;

namespace VolleyballClub.Client.Services;

/// <summary>학기별 부원 행 관리 API 클라이언트(설정>시트 설정 · 부원 상세 시트 연결 · 대량 추가 추천).</summary>
public sealed class SemesterMemberClient(ApiHttp api)
{
    public Task<List<SemesterMemberRowDto>> GetListAsync(int? semesterId = null, bool? mapped = null) =>
        api.GetAsync<List<SemesterMemberRowDto>>($"api/admin/semester-members?semesterId={semesterId}&mapped={mapped}");

    public Task<SemesterMemberRowDto> AddRowAsync(SemesterMemberCreateRequest request) =>
        api.PostAsync<SemesterMemberRowDto>("api/admin/semester-members", request);

    public Task RemoveRowAsync(int rowId) => api.DeleteAsync($"api/admin/semester-members/{rowId}");

    public Task SetMappingAsync(int rowId, string? userId) =>
        api.PostAsync($"api/admin/semester-members/{rowId}/mapping", new SemesterMemberMappingRequest(userId));

    public Task<SemesterRowAttendanceDto> GetRowAttendanceAsync(int rowId) =>
        api.GetAsync<SemesterRowAttendanceDto>($"api/admin/semester-members/{rowId}/attendance");

    public Task SetRowAttendanceAsync(int rowId, int activityId, bool attended) =>
        api.PutAsync($"api/admin/semester-members/{rowId}/attendance/{activityId}", new ActivityAttendanceRequest(attended));

    public Task<MemberSheetRowsDto> GetForMemberAsync(string userId) =>
        api.GetAsync<MemberSheetRowsDto>($"api/admin/members/{userId}/sheet-rows");
}
