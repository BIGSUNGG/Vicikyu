using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VolleyballClub.Application.Abstractions;
using VolleyballClub.Infrastructure.Identity;
using VolleyballClub.Infrastructure.Persistence;
using VolleyballClub.Infrastructure.Services;

namespace VolleyballClub.Infrastructure;

/// <summary>Composition Root(Web Program.cs)에서 호출하는 Infrastructure 등록 확장.</summary>
public static class DependencyInjection
{
    public const string AdminRole = "ADMIN";
    public const string MemberRole = "MEMBER";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

        // 서비스 호출마다 단기 컨텍스트를 사용한다.
        services.AddDbContextFactory<ApplicationDbContext>(options => options.UseSqlite(connectionString));

        services.AddIdentityCore<ApplicationUser>(options =>
            {
                options.SignIn.RequireConfirmedAccount = false;
                options.User.RequireUniqueEmail = false;
                // 비밀번호 규칙: 4자 이상이면 충분(복잡도 요구 없음)
                options.Password.RequiredLength = 4;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequireDigit = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireLowercase = false;
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders();

        services.AddSingleton<IDatabaseMaintenance>(new DatabaseMaintenance(connectionString));
        services.AddSingleton<IDatabaseSnapshot, SqliteDatabaseSnapshot>();
        services.AddScoped<IDatabaseBackupService, DatabaseBackupService>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ISheetClient, GoogleSheetClient>();
        services.AddSingleton<ISheetFormatResolver, SheetFormatResolver>();
        services.AddScoped<ISheetSyncService, SheetSyncService>();
        services.AddScoped<ISheetAdminService, SheetAdminService>();
        services.AddScoped<ISheetMemberMappingService, SheetMemberMappingService>();

        // 학기별 출석부 서식 전략(Design/sheet-formats.md) — 새 학기는 SemesterTabFormat.All()에 추가
        foreach (var format in SemesterTabFormat.All())
        {
            services.AddSingleton<ISemesterSheetFormat>(format);
        }
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUserService, CurrentUserService>();
        services.AddScoped<ISettingService, SettingService>();
        services.AddScoped<IAuditLogService, AuditLogService>();
        services.AddScoped<IActivityService, ActivityService>();
        services.AddScoped<IAttendanceService, AttendanceService>();
        services.AddScoped<ITeamService, TeamService>();
        services.AddScoped<IMemberService, MemberService>();
        services.AddSingleton<IMemberWorkbookReader, MemberWorkbookReader>();
        services.AddScoped<IFeeService, FeeService>();
        services.AddScoped<ISemesterService, SemesterService>();

        return services;
    }
}
