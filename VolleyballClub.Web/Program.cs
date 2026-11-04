using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Routing;
using VolleyballClub.Client.Components;
using VolleyballClub.Domain.Enums;
using VolleyballClub.Infrastructure.Identity;
using VolleyballClub.Web.Api;
using VolleyballClub.Web.Verification;
using Microsoft.AspNetCore.Identity;
using MudBlazor.Services;
using VolleyballClub.Application.Abstractions;
using VolleyballClub.Infrastructure;
using VolleyballClub.Infrastructure.Persistence;
using VolleyballClub.Infrastructure.Services;
using VolleyballClub.Web.Components;


var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveWebAssemblyComponents();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();

// EF Core(SQLite) + Identity + 애플리케이션 서비스 등록
builder.Services.AddInfrastructure(builder.Configuration);
if (builder.Environment.IsEnvironment(VerificationEnvironment.Name))
    VerificationEnvironment.Configure(builder);

builder.Services.AddMudServices();
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
    options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
    options.Events.OnValidatePrincipal = async context =>
    {
        var signIn = context.HttpContext.RequestServices.GetRequiredService<SignInManager<ApplicationUser>>();
        var user = await signIn.ValidateSecurityStampAsync(context.Principal);
        var roles = user is null ? [] : await signIn.UserManager.GetRolesAsync(user);
        var claimedRoles = context.Principal!.FindAll(System.Security.Claims.ClaimTypes.Role).Select(c => c.Value);
        if (user is null || user.Status != MemberStatus.Active || !roles.Order().SequenceEqual(claimedRoles.Order()))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
        }
    };
});

// 자동 DB 백업 — Testing 환경(통합 테스트 호스트)에서는 명시적 파일 수 기준 단언을 보호하기 위해 비활성화.
if (!builder.Environment.IsEnvironment("Testing"))
{
    // 컨테이너 교체(Watchtower 태그 배포)·재시작 직전 종료 백업 — 마이그레이션 전 스냅샷.
    // 역순 정지이므로 스케줄러보다 먼저 등록해 스케줄 루프가 정지된 뒤 종료 백업이 실행된다.
    builder.Services.AddHostedService<ShutdownBackupService>();
    builder.Services.AddHostedService<BackupScheduler>();
}

var app = builder.Build();
if (app.Services.GetRequiredService<IDatabaseMaintenance>().IsBlocked)
    throw new InvalidOperationException("DB 복원 중단/복구 실패 표식이 있습니다. database-backups 자동 백업을 확인하고 수동 복구 후 표식을 제거하세요.");

if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment(VerificationEnvironment.Name))
{
    // 마이그레이션 적용 + 시드 데이터(멱등)
    await DbInitializer.InitializeAsync(app.Services);
    await ActivityManagementVerification.SeedAsync(app);
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseWhen(context => !context.Request.Path.StartsWithSegments("/api"), branch =>
    branch.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true));
app.UseMiddleware<DatabaseMaintenanceMiddleware>();
if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment(VerificationEnvironment.Name))
    app.UseMigrationsEndPoint();
app.UseAuthentication();
app.UseMiddleware<ApiMiddleware>();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(Routes).Assembly)
    .AllowAnonymous();

app.MapAuthApi();
app.MapBusinessApi();
app.MapDatabaseBackupApi();
ActivityManagementVerification.MapEndpoints(app);

app.Run();

public partial class Program { }
