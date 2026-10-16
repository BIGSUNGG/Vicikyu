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

var app = builder.Build();
if (app.Services.GetRequiredService<IDatabaseMaintenance>().IsBlocked)
    throw new InvalidOperationException("DB 복원 중단/복구 실패 표식이 있습니다. database-backups 자동 백업을 확인하고 수동 복구 후 표식을 제거하세요.");

if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment(VerificationEnvironment.Name))
{
    // 마이그레이션 적용 + 시드 데이터(멱등)
    await DbInitializer.InitializeAsync(app.Services);
    await ActivityManagementVerification.SeedAsync(app);

    // 시트 출석부 동기화(시작 시 1회) — 실패해도 기동은 계속된다(DB 캐시는 이전 상태 유지)
    // TODO(배포 전): 프로덕션 환경에서도 동일하게 실행되어야 한다 — 이 블록을 IsDevelopment 밖으로 이동
    if (app.Environment.IsDevelopment() && builder.Configuration.GetValue<bool>("Sheets:SyncOnStartup", true))
    try
    {
        using var syncScope = app.Services.CreateScope();
        var report = await syncScope.ServiceProvider.GetRequiredService<ISheetSyncService>().SyncAsync();
        app.Logger.LogInformation(
            "시트 동기화: 활동 {ImportedActivities}건·출석 {ImportedAttendances}건 가져옴, 열 {ExportedColumns}개 내보냄, 미매칭 {SkippedUnmatched}명",
            report.ImportedActivities, report.ImportedAttendances, report.ExportedColumns, report.SkippedUnmatchedRows);
        foreach (var warning in report.Warnings)
        {
            app.Logger.LogWarning("시트 동기화: {Warning}", warning);
        }
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "시트 동기화 실패 — 구글 시트 구성/네트워크 확인. 다음 기동에 재시도됩니다.");
    }
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
