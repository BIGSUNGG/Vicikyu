using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Domain.Enums;
using VolleyballClub.Infrastructure.Identity;

namespace VolleyballClub.Web.Api;

public static class AuthEndpoints
{
    public static void MapAuthApi(this WebApplication app)
    {
        var auth = app.MapGroup("/api/auth");
        auth.MapGet("/csrf", (HttpContext context, IAntiforgery antiforgery) =>
            new CsrfTokenDto(antiforgery.GetAndStoreTokens(context).RequestToken!));
        auth.MapGet("/user", (HttpContext context) => context.User.Identity?.IsAuthenticated == true
            ? Results.Ok(new SessionUserDto(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!,
                context.User.Identity.Name ?? "", context.User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray()))
            : Results.Unauthorized());
        auth.MapPost("/login", async (LoginRequest request, UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn) =>
        {
            var user = string.IsNullOrWhiteSpace(request.StudentNumber) ? null : await users.FindByNameAsync(request.StudentNumber.Trim());
            if (user is null || user.Status != MemberStatus.Active || string.IsNullOrEmpty(request.Password))
                return LoginFailed();
            var result = await signIn.PasswordSignInAsync(user, request.Password, false, false);
            return result.Succeeded ? Results.NoContent() : LoginFailed();
        });
        auth.MapPost("/logout", async (SignInManager<ApplicationUser> signIn) =>
        {
            await signIn.SignOutAsync();
            return Results.NoContent();
        });
    }

    private static IResult LoginFailed() => Results.Json(new ApiErrorDto("login", "학번 또는 비밀번호가 올바르지 않거나 비활성화된 계정입니다."), statusCode: 400);
}
