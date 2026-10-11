using VolleyballClub.Application.Dtos;

namespace VolleyballClub.Application.Abstractions;

public interface IMemberService
{
    Task<List<MemberListDto>> GetListAsync(string? search = null);

    Task<MemberListDto> AddAsync(MemberCreateRequest request);

    Task<MemberListDto> UpdateAsync(string userId, MemberUpdateRequest request);

    Task<ProfileDto> GetProfileAsync(string userId);

    /// <summary>본인 색 변경 — MemberColors 팔레트 외 값은 RuleViolationException.</summary>
    Task UpdateMyColorAsync(string color);

    /// <summary>본인 비밀번호 변경 — 현재 비밀번호 불일치 시 RuleViolationException.</summary>
    Task ChangeMyPasswordAsync(PasswordChangeRequest request);
}
