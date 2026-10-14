using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using VolleyballClub.Application.Dtos;

namespace VolleyballClub.Client.Services;

public sealed class CookieAuthenticationStateProvider : AuthenticationStateProvider
{
    private readonly HttpClient http;
    private Task<AuthenticationState>? state;
    private static AuthenticationState Anonymous => new(new ClaimsPrincipal(new ClaimsIdentity()));

    public CookieAuthenticationStateProvider(HttpClient http, ApiHttp api)
    {
        this.http = http;
        api.Unauthorized += Clear;
        api.IdentityChanged += RefreshAsync;
    }

    public override Task<AuthenticationState> GetAuthenticationStateAsync() => state ??= LoadAsync();
    public async Task RefreshAsync()
    {
        state = LoadAsync();
        NotifyAuthenticationStateChanged(state);
        await state;
    }
    private void Clear()
    {
        state = Task.FromResult(Anonymous);
        NotifyAuthenticationStateChanged(state);
    }
    private async Task<AuthenticationState> LoadAsync()
    {
        using var response = await http.GetAsync("api/auth/user");
        if (response.StatusCode == HttpStatusCode.Unauthorized) return Anonymous;
        response.EnsureSuccessStatusCode();
        var user = (await response.Content.ReadFromJsonAsync<SessionUserDto>())!;
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, user.UserId), new(ClaimTypes.Name, user.Name) };
        claims.AddRange(user.Roles.Select(role => new Claim(ClaimTypes.Role, role)));
        return new(new ClaimsPrincipal(new ClaimsIdentity(claims, "cookie")));
    }
}
