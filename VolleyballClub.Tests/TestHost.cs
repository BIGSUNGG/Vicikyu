using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VolleyballClub.Application.Abstractions;
using VolleyballClub.Application.Exceptions;
using VolleyballClub.Domain.Entities;
using VolleyballClub.Domain.Enums;
using VolleyballClub.Infrastructure.Identity;
using VolleyballClub.Infrastructure.Persistence;
using VolleyballClub.Infrastructure.Services;

namespace VolleyballClub.Tests;

/// <summary>시트 동기화 스텁 — 마감·예정 내보내기 호출만 기록한다.</summary>
public sealed class FakeSheetSyncService : ISheetSyncService
{
    public SheetPushResult? LastPushResult { get; private set; }
    public List<int> PushedActivityIds { get; } = [];
    public Task<SheetPushResult> PushActivityAsync(int activityId, CancellationToken ct = default)
    {
        PushedActivityIds.Add(activityId);
        return Task.FromResult(LastPushResult = new SheetPushResult(true, true, null));
    }
    public Task<SheetSyncStatusDto> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(new SheetSyncStatusDto(0, []));

    public List<int> ExportedActivityIds { get; } = [];

    public List<int> ScheduledActivityIds { get; } = [];

    public Task<SheetSyncReport> SyncAsync(CancellationToken ct = default) =>
        Task.FromResult(new SheetSyncReport(0, 0, 0, 0, []));

    public Task<bool> ExportClosedActivityAsync(int activityId, CancellationToken ct = default)
    {
        ExportedActivityIds.Add(activityId);
        return Task.FromResult(true);
    }

    public Task<bool> ExportScheduledActivityAsync(int activityId, CancellationToken ct = default)
    {
        ScheduledActivityIds.Add(activityId);
        return Task.FromResult(true);
    }

    public Task<SheetSyncReport> ResyncAsync(CancellationToken ct = default) =>
        Task.FromResult(new SheetSyncReport(0, 0, 0, 0, []));

    public List<int> CancelledActivityIds { get; } = [];

    public Task<bool> RemoveCancelledActivityAsync(int activityId, CancellationToken ct = default)
    {
        CancelledActivityIds.Add(activityId);
        return Task.FromResult(true);
    }
}

/// <summary>시간을 고정하는 TimeProvider — '오늘'과 OTP 시각을 테스트에서 제어한다.</summary>
public sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(utcNow);

    public override DateTimeOffset GetUtcNow() => Now;

    public void MoveTo(DateOnly kstDate, int hour = 18) =>
        Now = new DateTimeOffset(DateTime.SpecifyKind(
            kstDate.ToDateTime(TimeOnly.FromTimeSpan(TimeSpan.FromHours(hour - 9))), // KST→UTC
            DateTimeKind.Utc));
}

/// <summary>권한/사용자 전환이 가능한 ICurrentUserService 스텁.</summary>
public sealed class FakeCurrentUser : ICurrentUserService
{
    public string? UserId { get; set; }

    public bool IsAdmin { get; set; }

    public FakeCurrentUser(string? userId = null, bool isAdmin = false)
    {
        UserId = userId;
        IsAdmin = isAdmin;
    }

    public void Become(string userId, bool isAdmin = false) => (UserId, IsAdmin) = (userId, isAdmin);

    public Task<string?> GetUserIdAsync() => Task.FromResult(UserId);

    public Task<bool> IsAdminAsync() => Task.FromResult(IsAdmin);

    public Task<string> RequireUserIdAsync() =>
        UserId is null ? throw new ForbiddenException("로그인이 필요합니다.") : Task.FromResult(UserId);

    public Task RequireAdminAsync()
    {
        if (!IsAdmin)
        {
            throw new ForbiddenException();
        }

        return Task.CompletedTask;
    }
}

public sealed class TestDb : IAsyncDisposable
{
    private readonly SqliteConnection connection;

    public IDbContextFactory<ApplicationDbContext> Factory { get; }

    public TestDb()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;
        Factory = new SimpleFactory(options);

        using var db = CreateContext();
        db.Database.EnsureCreated();

        // 서비스의 역할 검증을 위한 기본 역할
        db.Roles.Add(new IdentityRole { Name = "ADMIN", NormalizedName = "ADMIN" });
        db.Roles.Add(new IdentityRole { Name = "MEMBER", NormalizedName = "MEMBER" });
        db.SaveChanges();
    }

    public ApplicationDbContext CreateContext() => new(
        ((SimpleFactory)Factory).CreateDbContextOptions());

    public async ValueTask DisposeAsync()
    {
        await connection.DisposeAsync();
    }

    private sealed class SimpleFactory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
    {
        public DbContextOptions<ApplicationDbContext> CreateDbContextOptions() => options;

        public ApplicationDbContext CreateDbContext() => new(options);

        public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}

/// <summary>서비스들을 SQLite 인메모리 DB 위에 조립하는 테스트 호스트.</summary>
public sealed class ServiceHost : IAsyncDisposable
{
    public TestDb Db { get; }

    public FixedTimeProvider Time { get; }

    public FakeCurrentUser Current { get; }

    public IAuditLogService Audit { get; }

    public ISettingService Settings { get; }

    public IActivityService Activity { get; }

    public IAttendanceService Attendance { get; }

    public ITeamService Team { get; }

    public IFeeService Fee { get; }

    public ISemesterService Semester { get; }

    public FakeSheetSyncService SheetSync { get; } = new();
    public FakeSheetClient ManagedSheets { get; } = new();
    public async Task SeedManagedSheetAsync(string userId)
    {
        await using var db = Db.CreateContext();
        var sheet = new SheetDefinition { Name = "test attendance", SpreadsheetId = "managed-test", Kind = SheetKind.Attendance };
        db.SheetDefinitions.Add(sheet);
        await db.SaveChangesAsync();
        db.SheetMemberMappings.Add(new SheetMemberMapping { SheetDefinitionId = sheet.Id, TabName = "2026-2", UserId = userId, RowLabel = userId });
        await db.SaveChangesAsync();
        ManagedSheets.Tabs.Add(new VolleyballClub.Application.Dtos.SheetTab("2026-2", [["활동 상태"], ["부원"], [userId]]));
    }

    /// <summary>출석 서비스가 참조할 학기 전략 리졸버 — 기본은 전략 없음(모든 시트 활동이 비회원 학기가 됨).</summary>
    public ISheetFormatResolver FormatResolver { get; }

    public IMemberService Member { get; }

    /// <summary>테스트 검증용(비밀번호 확인 등) — 서비스와 별개 컨텍스트로 DB를 읽는다.</summary>
    public UserManager<ApplicationUser> Users { get; }

    public ServiceHost() : this(new FixedTimeProvider(new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc)), new FakeCurrentUser())
    {
    }

    public ServiceHost(FixedTimeProvider? time = null, FakeCurrentUser? current = null, ISheetFormatResolver? formatResolver = null)
    {
        Time = time ?? new FixedTimeProvider(new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc));
        Current = current ?? new FakeCurrentUser();
        FormatResolver = formatResolver ?? new SheetFormatResolver([]);
        Db = new TestDb();
        Audit = new AuditLogService(Db.Factory, Current, Time);
        Settings = new SettingService(Db.Factory, Current, Time);
        Activity = new ActivityService(Db.Factory, Current, Audit, SheetSync, Time);
        Attendance = new AttendanceService(Db.Factory, Current, Audit, FormatResolver, Time,
            new AdminActivityService(Db.Factory, ManagedSheets, new SheetFormatResolver(SemesterTabFormat.All()), Current, SheetSync, Time), SheetSync);
        Team = new TeamService(Db.Factory, Current, Audit, Time);
        Fee = new FeeService(Db.Factory, Current, Audit, Time);
        Semester = new SemesterService(Db.Factory, Current, Audit, Time);
        var memberDb = Db.CreateContext();
        Member = new MemberService(Db.Factory, CreateUserManager(memberDb), Current, Audit, FormatResolver, Time, memberDb);
        Users = CreateUserManager();
    }

    private UserManager<ApplicationUser> CreateUserManager(ApplicationDbContext? context = null)
    {
        var db = context ?? Db.CreateContext();
        return new UserManager<ApplicationUser>(
            new UserStore<ApplicationUser>(db),
            Options.Create(new IdentityOptions
            {
                // 앱 구성(DependencyInjection)과 동일한 완화 정책 미러링
                Password = new PasswordOptions
                {
                    RequiredLength = 4,
                    RequireNonAlphanumeric = false,
                    RequireDigit = false,
                    RequireUppercase = false,
                    RequireLowercase = false,
                },
            }),
            new PasswordHasher<ApplicationUser>(),
            [], [],
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            null,
            NullLogger<UserManager<ApplicationUser>>.Instance);
    }

    public ServiceHost AsAdmin(string userId = "admin-1")
    {
        Current.Become(userId, isAdmin: true);
        return this;
    }

    public ServiceHost AsMember(string userId)
    {
        Current.Become(userId, isAdmin: false);
        return this;
    }

    /// <summary>테스트용 부원을 직접 DB에 삽입하고 Id를 반환한다.</summary>
    public async Task<string> SeedUserAsync(string name, string studentNumber, MemberStatus status = MemberStatus.Active)
    {
        await using var db = Db.CreateContext();
        var user = new ApplicationUser
        {
            Id = $"user-{studentNumber}",
            UserName = studentNumber,
            NormalizedUserName = studentNumber.ToUpperInvariant(),
            StudentNumber = studentNumber,
            Name = name,
            Department = "테스트학과",
            Status = status,
            JoinedAt = new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    public async Task<Activity> SeedActivityAsync(DateOnly date, ActivityStatus status = ActivityStatus.AttendanceOpen, string? otp = null)
    {
        await using var db = Db.CreateContext();
        var activity = new Activity
        {
            ActivityDate = date,
            Status = status,
            OtpCode = otp,
            AttendanceOpenedAt = DateTime.UtcNow,
            CreatedByUserId = "admin-1",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Activities.Add(activity);
        await db.SaveChangesAsync();
        return activity;
    }

    public async Task<Activity> GetTodayActivityAsync()
    {
        var today = Domain.KstClock.Today(Time);
        await using var db = Db.CreateContext();
        return await db.Activities.FirstAsync(a => a.ActivityDate == today);
    }

    public async Task<int> CountAttendancesAsync()
    {
        await using var db = Db.CreateContext();
        return await db.Attendances.CountAsync();
    }

    public ValueTask DisposeAsync() => Db.DisposeAsync();
}
