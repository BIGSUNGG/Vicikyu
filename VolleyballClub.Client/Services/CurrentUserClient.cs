using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using VolleyballClub.Application.Exceptions;

namespace VolleyballClub.Client.Services;

public sealed class CurrentUserClient(AuthenticationStateProvider state)
{
    public async Task<string> RequireUserIdAsync() =>
        (await state.GetAuthenticationStateAsync()).User.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? throw new ForbiddenException("로그인이 필요합니다.");
}
