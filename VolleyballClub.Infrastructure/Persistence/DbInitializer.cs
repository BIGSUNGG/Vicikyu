using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VolleyballClub.Domain;
using VolleyballClub.Domain.Entities;
using VolleyballClub.Domain.Enums;
using VolleyballClub.Infrastructure.Identity;
using VolleyballClub.Infrastructure.Persistence;
using VolleyballClub.Infrastructure.Services;

namespace VolleyballClub.Infrastructure.Persistence;

/// <summary>개발 환경 시작 시 마이그레이션 적용 + 시드 데이터(멱등).</summary>
public static class DbInitializer
{
    public static async Task InitializeAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;

        var dbFactory = sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Database.MigrateAsync();

        var roleManager = sp.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var role in new[] { "ADMIN", "MEMBER" })
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                await roleManager.CreateAsync(new IdentityRole(role));
            }
        }

        // 색 사용량 집계 — 백필/시드 모두 이 딕셔너리를 갱신하며 골고루 분산 배정한다
        var colorUsage = await db.Users.Where(u => u.Color != null)
            .GroupBy(u => u.Color!)
            .ToDictionaryAsync(g => g.Key, g => g.Count());
        string NextColor()
        {
            var color = MemberColors.PickBalanced(colorUsage);
            colorUsage[color] = colorUsage.GetValueOrDefault(color) + 1;
            return color;
        }

        // 레거시 부원(색 미지정) 백필 — 멱등: 색 있는 부원은 건드리지 않는다
        var noColorUsers = await db.Users.Where(u => u.Color == null).ToListAsync();
        foreach (var user in noColorUsers)
        {
            user.Color = NextColor();
        }
        if (noColorUsers.Count > 0)
        {
            await db.SaveChangesAsync();
        }

        if (await db.Users.AnyAsync())
        {
            return; // 이미 시드됨
        }

        var userManager = sp.GetRequiredService<UserManager<ApplicationUser>>();
        var adminPassword = sp.GetRequiredService<IConfiguration>()["Seed:AdminPassword"]
            ?? throw new InvalidOperationException("Seed password 'Seed:AdminPassword' not found.");
        var timeProvider = sp.GetRequiredService<TimeProvider>();
        var now = KstClock.NowUtc(timeProvider);

        // 관리자
        var admin = new ApplicationUser
        {
            UserName = "admin",
            StudentNumber = "admin",
            Name = "김회장",
            Department = "컴퓨터공학과",
            Color = NextColor(),
            JoinedAt = new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc),
            CreatedAt = now,
            UpdatedAt = now,
        };
        await userManager.CreateAsync(admin, adminPassword);
        await userManager.AddToRoleAsync(admin, "ADMIN");
    }
}
