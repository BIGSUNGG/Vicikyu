using VolleyballClub.Application.Exceptions;

namespace VolleyballClub.Application.Abstractions;

/// <summary>현재 로그인 사용자 정보 접근 + 서비스 레벨 권한 검증(화면 숨김과 별개의 서버 검증).</summary>
public interface ICurrentUserService
{
    Task<string?> GetUserIdAsync();

    Task<bool> IsAdminAsync();

    Task<string> RequireUserIdAsync();

    /// <summary>관리자가 아니면 ForbiddenException을 던진다.</summary>
    Task RequireAdminAsync();
}
