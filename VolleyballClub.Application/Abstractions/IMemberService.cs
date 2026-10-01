using VolleyballClub.Application.Dtos;

namespace VolleyballClub.Application.Abstractions;

public interface IMemberService
{
    Task<List<MemberListDto>> GetListAsync(string? search = null);

    Task<MemberListDto> AddAsync(MemberCreateRequest request);

    Task<MemberListDto> UpdateAsync(string userId, MemberUpdateRequest request);

    Task<ProfileDto> GetProfileAsync(string userId);
}
