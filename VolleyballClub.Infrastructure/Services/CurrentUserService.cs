using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using VolleyballClub.Application.Abstractions;
using VolleyballClub.Application.Exceptions;

namespace VolleyballClub.Infrastructure.Services;

/// <summary>
/// Blazor 인증 상태에서 현재 사용자 정보를 제공하고, 관리자 서비스의 서버 레벨 권한을 검증한다.
/// 화면(AuthorizeView)과 무관하게 서비스 계층에서 반드시 검증된다.
/// </summary>
public sealed class CurrentUserService(AuthenticationStateProvider authenticationStateProvider) : ICurrentUserService
{
    public async Task<string?> GetUserIdAsync()
    {
        var state = await authenticationStateProvider.GetAuthenticationStateAsync();
        return state.User.FindFirstValue(ClaimTypes.NameIdentifier);
    }

    public async Task<bool> IsAdminAsync()
    {
        var state = await authenticationStateProvider.GetAuthenticationStateAsync();
        return state.User.IsInRole("ADMIN");
    }

    public async Task<string> RequireUserIdAsync()
    {
        var userId = await GetUserIdAsync();
        return userId ?? throw new ForbiddenException("로그인이 필요합니다.");
    }

    public async Task RequireAdminAsync()
    {
        if (!await IsAdminAsync())
        {
            throw new ForbiddenException();
        }
    }
}
