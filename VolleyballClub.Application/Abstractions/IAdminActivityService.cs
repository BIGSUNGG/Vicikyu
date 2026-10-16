using VolleyballClub.Application.Dtos;
namespace VolleyballClub.Application.Abstractions;
public interface IAdminActivityService
{
    Task<IReadOnlyList<ActivityTargetDto>> GetTargetsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ManagedActivityDto>> GetActivitiesAsync(CancellationToken ct = default);
    Task<ManagedActivityDto> CreateAsync(ActivityCreateRequest request, CancellationToken ct = default);
    Task<ManagedActivityDto> EditAsync(string key, ActivityEditRequest request, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
    Task<IReadOnlyList<AttendanceMemberDto>> GetMembersAsync(string key, CancellationToken ct = default);
    Task SetAttendanceAsync(string key, string userId, ActivityAttendanceRequest request, CancellationToken ct = default);
    Task<MemberActivityListDto> GetMemberActivitiesAsync(string userId, CancellationToken ct = default);
}
