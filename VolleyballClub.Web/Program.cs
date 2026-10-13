using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using MudBlazor.Services;
using VolleyballClub.Application.Abstractions;
using VolleyballClub.Infrastructure;
using VolleyballClub.Infrastructure.Persistence;
using VolleyballClub.Web.Components;
using VolleyballClub.Web.Components.Account;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();

// EF Core(SQLite) + Identity + 애플리케이션 서비스 등록
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddMudServices();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();

    // 마이그레이션 적용 + 시드 데이터(멱등)
    await DbInitializer.InitializeAsync(app.Services);

    // 시트 출석부 동기화(시작 시 1회) — 실패해도 기동은 계속된다(DB 캐시는 이전 상태 유지)
    // TODO(배포 전): 프로덕션 환경에서도 동일하게 실행되어야 한다 — 이 블록을 IsDevelopment 밖으로 이동
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

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapAdditionalIdentityEndpoints();

app.Run();
