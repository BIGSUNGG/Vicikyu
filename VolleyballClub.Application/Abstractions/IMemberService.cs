using VolleyballClub.Application.Dtos;

namespace VolleyballClub.Application.Abstractions;

public interface IMemberService
{
    /// <summary>부원 목록 — 서버 쿼리 필터링·정렬·페이징.</summary>
    Task<MemberPageDto> GetPageAsync(MemberPageRequest request);

    /// <summary>부원 1명. 없으면 NotFoundException.</summary>
    Task<MemberListDto> GetAsync(string userId);

    Task<MemberListDto> AddAsync(MemberCreateRequest request);

    Task<MemberListDto> UpdateAsync(string userId, MemberUpdateRequest request);

    Task<ProfileDto> GetProfileAsync(string userId);

    /// <summary>본인 색 변경 — MemberColors 팔레트 외 값은 RuleViolationException.</summary>
    Task UpdateMyColorAsync(string color);

    /// <summary>본인 비밀번호 변경 — 현재 비밀번호 불일치 시 RuleViolationException.</summary>
    Task ChangeMyPasswordAsync(PasswordChangeRequest request);
}
