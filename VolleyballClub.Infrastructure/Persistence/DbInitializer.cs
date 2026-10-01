using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VolleyballClub.Domain;
using VolleyballClub.Domain.Entities;
using VolleyballClub.Domain.Enums;
using VolleyballClub.Infrastructure.Identity;
using VolleyballClub.Infrastructure.Persistence;

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

        if (await db.Users.AnyAsync())
        {
            return; // 이미 시드됨
        }

        var userManager = sp.GetRequiredService<UserManager<ApplicationUser>>();
        var timeProvider = sp.GetRequiredService<TimeProvider>();
        var now = KstClock.NowUtc(timeProvider);
        var today = KstClock.Today(timeProvider);

        // 설정 기본값
        db.Settings.AddRange(
            new Setting { Key = "SchoolName", Value = "OO대학교", UpdatedAt = now },
            new Setting { Key = "ClubName", Value = "배구부", UpdatedAt = now },
            new Setting { Key = "FeeSheetUrl", Value = "https://docs.google.com/spreadsheets/", UpdatedAt = now },
            new Setting { Key = "DefaultTeamCount", Value = "3", UpdatedAt = now },
            new Setting { Key = "OtpLength", Value = "4", UpdatedAt = now });
        await db.SaveChangesAsync();

        // 관리자
        var admin = new ApplicationUser
        {
            UserName = "admin",
            StudentNumber = "admin",
            Name = "김회장",
            Department = "컴퓨터공학과",
            IsActive = true,
            JoinedAt = new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc),
            CreatedAt = now,
            UpdatedAt = now,
        };
        await userManager.CreateAsync(admin, "Admin1234!");
        await userManager.AddToRoleAsync(admin, "ADMIN");

        // 일반 부원 15명
        var memberSeed = new (string Name, string Department)[]
        {
            ("김민수", "컴퓨터공학과"),
            ("박지훈", "경영학과"),
            ("이정우", "체육학과"),
            ("홍길동", "국어국문학과"),
            ("최민호", "전자공학과"),
            ("이서준", "화학과"),
            ("김수진", "심리학과"),
            ("박민수", "기계공학과"),
            ("정우성", "사회학과"),
            ("윤태호", "수학과"),
            ("김지훈", "법학과"),
            ("최수진", "생명공학과"),
            ("박서준", "경제학과"),
            ("이민호", "정보통신공학과"),
            ("정태영", "철학과"),
        };

        var members = new List<ApplicationUser>();
        for (var i = 0; i < memberSeed.Length; i++)
        {
            var (name, department) = memberSeed[i];
            var studentNumber = $"2025120{i + 1:00}";
            var user = new ApplicationUser
            {
                UserName = studentNumber,
                StudentNumber = studentNumber,
                Name = name,
                Department = department,
                IsActive = true,
                JoinedAt = new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc),
                CreatedAt = now,
                UpdatedAt = now,
            };
            await userManager.CreateAsync(user, "Member1234!");
            await userManager.AddToRoleAsync(user, "MEMBER");
            members.Add(user);
        }

        // 오늘 활동 + 출석 샘플(팀 편성 즉시 테스트 가능)
        var activity = new Activity
        {
            ActivityDate = today,
            Status = ActivityStatus.AttendanceOpen,
            OtpCode = RandomNumberGenerator.GetInt32(1000, 10000).ToString(),
            AttendanceOpenedAt = now.AddHours(-1),
            CreatedByUserId = admin.Id,
            CreatedAt = now.AddHours(-1),
            UpdatedAt = now,
        };
        db.Activities.Add(activity);
        await db.SaveChangesAsync();

        var baseTime = new DateTime(today, new TimeOnly(18, 2), DateTimeKind.Utc);
        var minutes = new[] { 3, 5, 7, 9, 12, 14, 16, 19, 22, 25, 28, 31 };
        for (var i = 0; i < 12 && i < members.Count; i++)
        {
            db.Attendances.Add(new Attendance
            {
                ActivityId = activity.Id,
                UserId = members[i].Id,
                AttendanceType = i % 4 == 0 ? AttendanceType.Admin : AttendanceType.Otp,
                CheckedAt = baseTime.AddMinutes(minutes[i]),
                CreatedAt = baseTime.AddMinutes(minutes[i]),
            });
        }

        // 회비 기간 + 납부 샘플
        var (semStart, semEnd) = Semester.Of(today);
        var semesterLabel = Semester.Label(today);
        var feePeriod = new FeePeriod
        {
            Name = semesterLabel,
            StartDate = semStart,
            EndDate = semEnd,
            FeeAmount = 30_000,
            CreatedAt = now,
        };
        db.FeePeriods.Add(feePeriod);
        await db.SaveChangesAsync();

        for (var i = 0; i < members.Count; i++)
        {
            db.FeePayments.Add(new FeePayment
            {
                FeePeriodId = feePeriod.Id,
                UserId = members[i].Id,
                IsPaid = i < 11,
                PaidAt = i < 11 ? now.AddDays(-(i + 2)) : null,
                UpdatedByUserId = admin.Id,
                UpdatedAt = now,
            });
        }

        await db.SaveChangesAsync();
    }
}
