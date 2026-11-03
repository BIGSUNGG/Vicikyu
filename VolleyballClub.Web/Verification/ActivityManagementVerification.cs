using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using VolleyballClub.Application.Abstractions;
using VolleyballClub.Domain;
using VolleyballClub.Domain.Entities;
using VolleyballClub.Domain.Enums;
using VolleyballClub.Infrastructure.Identity;
using VolleyballClub.Infrastructure.Persistence;

namespace VolleyballClub.Web.Verification;

internal static class ActivityManagementVerification
{
    internal static bool Enabled(IHostEnvironment environment, IConfiguration configuration) =>
        environment.IsEnvironment(VerificationEnvironment.Name) && configuration.GetValue<bool>("Verification:ActivityManagement");

    internal sealed class FixtureClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);
    }

    internal static async Task SeedAsync(WebApplication app)
    {
        if (!Enabled(app.Environment, app.Configuration)) return;
        using var scope = app.Services.CreateScope();
        var sp = scope.ServiceProvider;
        await using var db = await sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContextAsync();
        if (db.Database.GetConnectionString() != VerificationEnvironment.Connection)
            throw new InvalidOperationException("Activity verification requires an isolated seeded database.");
        var users = await db.Users.OrderBy(u => u.StudentNumber).ToListAsync();
        if (users.Count == 1 && users[0].StudentNumber == "admin")
        {
            // 검증 환경은 자기 픽스처를 여기서 자급한다(격리 DB 전용).
            var userManager = sp.GetRequiredService<UserManager<ApplicationUser>>();
            var seededAt = KstClock.NowUtc(sp.GetRequiredService<TimeProvider>());
            var memberSeed = new (string Name, string Department)[]
            {
                ("김민수", "컴퓨터공학과"), ("박지훈", "경영학과"), ("이정우", "체육학과"), ("홍길동", "국어국문학과"),
                ("최민호", "전자공학과"), ("이서준", "화학과"), ("김수진", "심리학과"), ("박민수", "기계공학과"),
                ("정우성", "사회학과"), ("윤태호", "수학과"), ("김지훈", "법학과"), ("최수진", "생명공학과"),
                ("박서준", "경제학과"), ("이민호", "정보통신공학과"), ("정태영", "철학과"),
            };
            for (var i = 0; i < memberSeed.Length; i++)
            {
                var (name, department) = memberSeed[i];
                var studentNumber = $"2025120{i + 1:00}";
                var user = new ApplicationUser
                {
                    UserName = studentNumber, StudentNumber = studentNumber, Name = name, Department = department,
                    JoinedAt = new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc), CreatedAt = seededAt, UpdatedAt = seededAt,
                };
                await userManager.CreateAsync(user, "Member1234!");
                await userManager.AddToRoleAsync(user, "MEMBER");
            }

            // 검증 기준일(2026-10-16) 진행 활동 + 출석 샘플
            var today = KstClock.Today(sp.GetRequiredService<TimeProvider>());
            var live = new Activity
            {
                ActivityDate = today, Status = ActivityStatus.AttendanceOpen, OtpCode = "1234",
                AttendanceOpenedAt = seededAt.AddHours(-1), CreatedByUserId = users[0].Id,
                CreatedAt = seededAt.AddHours(-1), UpdatedAt = seededAt,
            };
            db.Activities.Add(live);
            await db.SaveChangesAsync();
            var memberIds = await db.Users.Where(u => u.StudentNumber != "admin").OrderBy(u => u.StudentNumber).Select(u => u.Id).ToListAsync();
            var baseTime = new DateTime(today, new TimeOnly(18, 2), DateTimeKind.Utc);
            var minutes = new[] { 3, 5, 7, 9, 12, 14, 16, 19, 22, 25, 28, 31 };
            for (var i = 0; i < minutes.Length; i++)
                db.Attendances.Add(new Attendance
                {
                    ActivityId = live.Id, UserId = memberIds[i],
                    AttendanceType = i % 4 == 0 ? AttendanceType.Admin : AttendanceType.Otp,
                    CheckedAt = baseTime.AddMinutes(minutes[i]), CreatedAt = baseTime.AddMinutes(minutes[i]),
                });
            await db.SaveChangesAsync();

            // 과거 완료 활동(팀·게스트 포함) + 예정/없음 표시 + 라벨 활동
            var completed = new Activity
            {
                ActivityDate = new DateOnly(2026, 10, 1), Status = ActivityStatus.ActivityClosed,
                CreatedByUserId = Activity.ImportCreator, CreatedAt = seededAt, UpdatedAt = seededAt,
                AttendanceOpenedAt = seededAt, AttendanceClosedAt = seededAt, ActivityClosedAt = seededAt,
            };
            var labeled = new Activity
            {
                ActivityDate = new DateOnly(2026, 10, 2), DisplayName = "OT", Status = ActivityStatus.ActivityClosed,
                CreatedByUserId = Activity.ImportCreator, CreatedAt = seededAt, UpdatedAt = seededAt,
                AttendanceOpenedAt = seededAt, AttendanceClosedAt = seededAt, ActivityClosedAt = seededAt,
            };
            db.Activities.AddRange(completed, labeled);
            db.DayMarks.AddRange(
                new DayMark { Date = new DateOnly(2026, 10, 20), Kind = DayKind.Scheduled },
                new DayMark { Date = new DateOnly(2026, 10, 22), Kind = DayKind.NoActivity, Note = "시험" });
            await db.SaveChangesAsync();
            db.Attendances.Add(new Attendance
            {
                ActivityId = completed.Id, UserId = memberIds[0], AttendanceType = AttendanceType.Import,
                CheckedAt = seededAt, CreatedAt = seededAt,
            });
            db.Attendances.Add(new Attendance
            {
                ActivityId = labeled.Id, UserId = memberIds[1], AttendanceType = AttendanceType.Import,
                CheckedAt = seededAt, CreatedAt = seededAt,
            });
            await db.SaveChangesAsync();
            var guest = new Guest { ActivityId = completed.Id, Name = "합성 게스트" };
            db.Guests.Add(guest);
            await db.SaveChangesAsync();
            db.Teams.Add(new Team { ActivityId = completed.Id, Name = "합성 팀", Members = [new TeamMember { GuestId = guest.Id }] });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            users = await db.Users.OrderBy(u => u.StudentNumber).ToListAsync();
        }
        if (users.Count != 16 || users.Count(u => u.StudentNumber == "admin") != 1 || users.Count(u => u.StudentNumber.StartsWith("2025120")) != 15)
            throw new InvalidOperationException("Activity verification requires only the standard synthetic seed members.");
    }

    internal static void MapEndpoints(WebApplication app)
    {
        if (!Enabled(app.Environment, app.Configuration)) return;
        var group = app.MapGroup("/api/verification/activity-management")
            .RequireAuthorization(policy => policy.RequireRole("ADMIN"));
        // Read-only evidence: never invokes a mutation service or repairs state on the verifier's behalf.
        group.MapGet("/state", async (IDbContextFactory<ApplicationDbContext> factory) =>
        {
            await using var db = await factory.CreateDbContextAsync();
            return Results.Json(new
            {
                clock = "2026-10-16T09:00:00Z", database = db.Database.GetConnectionString(),
                members = await db.Users.OrderBy(u => u.StudentNumber).Select(u => new { u.Id, u.StudentNumber, u.Name }).ToListAsync(),
                dayMarks = await db.DayMarks.OrderBy(m => m.Date).Select(m => new { m.Date, Kind = m.Kind.ToString(), m.Note }).ToListAsync(),
                activities = await db.Activities.OrderBy(a => a.Id).Select(a => new { a.Id, a.ActivityDate, a.DisplayName, Status = a.Status.ToString() }).ToListAsync(),
                attendances = await db.Attendances.OrderBy(a => a.Id).Select(a => new { a.Id, a.ActivityId, a.UserId }).ToListAsync(),
                guests = await db.Guests.OrderBy(g => g.Id).Select(g => new { g.Id, g.ActivityId }).ToListAsync(),
                teams = await db.Teams.OrderBy(t => t.Id).Select(t => new { t.Id, t.ActivityId }).ToListAsync(),
                teamMembers = await db.TeamMembers.OrderBy(t => t.Id).Select(t => new { t.Id, t.TeamId, t.UserId, t.GuestId }).ToListAsync(),
                unmappedMembers = await db.UnmappedMembers.OrderBy(m => m.Id).Select(m => new { m.Id, m.Label, m.Name }).ToListAsync(),
                semesters = await db.SemesterPeriods.OrderBy(s => s.Id).Select(s => new { s.Id, s.Name }).ToListAsync(),
                auditCount = await db.AuditLogs.CountAsync()
            });
        });
    }
}
