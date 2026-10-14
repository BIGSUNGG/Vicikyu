using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using VolleyballClub.Application.Abstractions;
using VolleyballClub.Application.Exceptions;

namespace VolleyballClub.Infrastructure.Services;

/// <summary>HTTP 인증 주체를 사용한다. 서비스 권한 검증은 화면 표시와 별개다.</summary>
public sealed class CurrentUserService(IHttpContextAccessor accessor) : ICurrentUserService
{
    public Task<string?> GetUserIdAsync() => Task.FromResult(accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier));
    public Task<bool> IsAdminAsync() => Task.FromResult(accessor.HttpContext?.User.IsInRole("ADMIN") == true);
    public async Task<string> RequireUserIdAsync() => await GetUserIdAsync() ?? throw new ForbiddenException("로그인이 필요합니다.");
    public async Task RequireAdminAsync()
    {
        if (!await IsAdminAsync()) throw new ForbiddenException();
    }
}
