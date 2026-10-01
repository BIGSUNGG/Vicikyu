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

        // Blazor Interactive Server에서 DbContext를 회로에 오래 들고 있지 않도록 팩토리 사용(호출마다 단기 컨텍스트)
        services.AddDbContextFactory<ApplicationDbContext>(options => options.UseNpgsql(connectionString));

        services.AddIdentityCore<ApplicationUser>(options =>
            {
                options.SignIn.RequireConfirmedAccount = false;
                options.User.RequireUniqueEmail = false;
                options.Password.RequiredLength = 8;
                options.Password.RequireNonAlphanumeric = false;
                options.Lockout.MaxFailedAccessAttempts = 5;
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders();

        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ICurrentUserService, CurrentUserService>();
        services.AddScoped<ISettingService, SettingService>();
        services.AddScoped<IAuditLogService, AuditLogService>();
        services.AddScoped<IActivityService, ActivityService>();
        services.AddScoped<IAttendanceService, AttendanceService>();
        services.AddScoped<ITeamService, TeamService>();
        services.AddScoped<IMemberService, MemberService>();
        services.AddScoped<IFeeService, FeeService>();

        return services;
    }
}
