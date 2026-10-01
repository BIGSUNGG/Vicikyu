using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using VolleyballClub.Application.Abstractions;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Application.Exceptions;
using VolleyballClub.Domain;
using VolleyballClub.Domain.Entities;
using VolleyballClub.Domain.Enums;
using VolleyballClub.Infrastructure.Persistence;

namespace VolleyballClub.Infrastructure.Services;

public class ActivityService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ICurrentUserService currentUser,
    IAuditLogService auditLog,
    ISettingService settings,
    TimeProvider timeProvider) : IActivityService
{
    public async Task<AdminTodayDto> GetOrCreateTodayAsync()
    {
        await currentUser.RequireAdminAsync();
        var today = KstClock.Today(timeProvider);
        await using var db = await dbFactory.CreateDbContextAsync();
        var activity = await db.Activities.FirstOrDefaultAsync(a => a.ActivityDate == today);
        if (activity is null)
        {
            var adminId = await currentUser.RequireUserIdAsync();
            var now = KstClock.NowUtc(timeProvider);
            activity = new Activity
            {
                ActivityDate = today,
                Status = ActivityStatus.Ready,
                CreatedByUserId = adminId,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Activities.Add(activity);
            await db.SaveChangesAsync();
            await auditLog.LogAsync(AdminAction.ActivityCreate, "Activity", activity.Id.ToString(), $"{today:yyyy-MM-dd} 활동 생성");
        }

        return await LoadAdminDtoAsync(db, activity.Id);
    }

    public async Task<AdminTodayDto> GetTodayForAdminAsync()
    {
        await currentUser.RequireAdminAsync();
        var today = KstClock.Today(timeProvider);
        await using var db = await dbFactory.CreateDbContextAsync();
        var activity = await FindTodayAsync(db, today);
        return await LoadAdminDtoAsync(db, activity.Id);
    }

    public async Task<MyTodayDto> GetMyTodayAsync(string userId)
    {
        var today = KstClock.Today(timeProvider);
        await using var db = await dbFactory.CreateDbContextAsync();
        var activity = await FindTodayAsync(db, today);

        var attendance = await db.Attendances.AsNoTracking()
            .Where(a => a.ActivityId == activity.Id && a.UserId == userId)
            .Select(a => new { a.CheckedAt, a.AttendanceType })
            .FirstOrDefaultAsync();

        var attendanceCount = await CountAttendanceAsync(db, activity.Id);
        var totalMembers = await db.Users.CountAsync(u => u.IsActive);

        return new MyTodayDto(
            activity.Id,
            activity.ActivityDate,
            activity.Status,
            StatusText(activity.Status),
            attendance is not null,
            attendance?.CheckedAt,
            attendance?.AttendanceType,
            attendanceCount,
            totalMembers);
    }

    public async Task<string> GenerateOtpAsync()
    {
        await currentUser.RequireAdminAsync();
        var today = KstClock.Today(timeProvider);
        await using var db = await dbFactory.CreateDbContextAsync();
        var activity = await FindTodayAsync(db, today);

        var length = Math.Clamp(await settings.GetIntAsync("OtpLength", 4), 4, 8);
        var max = (int)Math.Pow(10, length);
        var code = RandomNumberGenerator.GetInt32(0, max).ToString().PadLeft(length, '0');

        activity.OtpCode = code;
        activity.UpdatedAt = KstClock.NowUtc(timeProvider);
        await db.SaveChangesAsync();
        await auditLog.LogAsync(AdminAction.OtpChange, "Activity", activity.Id.ToString(), "당일 OTP 생성/변경");
        return code;
    }

    public async Task<AdminTodayDto> StartAttendanceAsync()
    {
        await currentUser.RequireAdminAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var activity = await FindTodayAsync(db, KstClock.Today(timeProvider));
        if (!activity.CanStartAttendance)
        {
            throw new ConflictException("현재 상태에서는 출석을 시작할 수 없습니다.");
        }

        if (string.IsNullOrEmpty(activity.OtpCode))
        {
            var length = Math.Clamp(await settings.GetIntAsync("OtpLength", 4), 4, 8);
            var max = (int)Math.Pow(10, length);
            activity.OtpCode = RandomNumberGenerator.GetInt32(0, max).ToString().PadLeft(length, '0');
        }

        var now = KstClock.NowUtc(timeProvider);
        activity.Status = ActivityStatus.AttendanceOpen;
        activity.AttendanceOpenedAt = now;
        activity.UpdatedAt = now;
        await db.SaveChangesAsync();
        await auditLog.LogAsync(AdminAction.AttendanceOpen, "Activity", activity.Id.ToString(), "출석 시작");

        return await LoadAdminDtoAsync(db, activity.Id);
    }

    public async Task<AdminTodayDto> CloseAttendanceAsync()
    {
        await currentUser.RequireAdminAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var activity = await FindTodayAsync(db, KstClock.Today(timeProvider));
        if (!activity.CanCloseAttendance)
        {
            throw new ConflictException("현재 상태에서는 출석을 마감할 수 없습니다.");
        }

        var now = KstClock.NowUtc(timeProvider);
        activity.Status = ActivityStatus.AttendanceClosed;
        activity.AttendanceClosedAt = now;
        activity.UpdatedAt = now;
        await db.SaveChangesAsync();
        await auditLog.LogAsync(AdminAction.AttendanceClose, "Activity", activity.Id.ToString(), "출석 마감");

        return await LoadAdminDtoAsync(db, activity.Id);
    }

    public async Task<AdminTodayDto> CloseActivityAsync()
    {
        await currentUser.RequireAdminAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var activity = await FindTodayAsync(db, KstClock.Today(timeProvider));
        if (!activity.CanClose)
        {
            throw new ConflictException("이미 종료된 활동입니다.");
        }

        var now = KstClock.NowUtc(timeProvider);
        activity.Status = ActivityStatus.ActivityClosed;
        activity.ActivityClosedAt = now;
        if (activity.AttendanceOpenedAt is null)
        {
            activity.AttendanceOpenedAt = now;
        }

        if (activity.AttendanceClosedAt is null)
        {
            activity.AttendanceClosedAt = now;
        }

        activity.UpdatedAt = now;
        await db.SaveChangesAsync();
        await auditLog.LogAsync(AdminAction.ActivityClose, "Activity", activity.Id.ToString(), "오늘 활동 마감");

        return await LoadAdminDtoAsync(db, activity.Id);
    }

    public async Task<AdminTodayDto> ReopenActivityAsync()
    {
        await currentUser.RequireAdminAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var activity = await FindTodayAsync(db, KstClock.Today(timeProvider));
        if (!activity.CanReopen)
        {
            throw new ConflictException("종료된 활동만 다시 열 수 있습니다.");
        }

        var now = KstClock.NowUtc(timeProvider);
        // 팀 편성 데이터가 있으면 TeamCreated, 없으면 AttendanceClosed 상태로 되돌린다
        var hasTeams = await db.Teams.AnyAsync(t => t.ActivityId == activity.Id);
        activity.Status = hasTeams ? ActivityStatus.TeamCreated : ActivityStatus.AttendanceClosed;
        activity.ActivityClosedAt = null;
        activity.UpdatedAt = now;
        await db.SaveChangesAsync();
        await auditLog.LogAsync(AdminAction.ActivityReopen, "Activity", activity.Id.ToString(), "활동 다시 열기");

        return await LoadAdminDtoAsync(db, activity.Id);
    }

    internal static async Task<Activity> FindTodayAsync(ApplicationDbContext db, DateOnly today) =>
        await db.Activities.FirstOrDefaultAsync(a => a.ActivityDate == today)
        ?? throw new NotFoundException("오늘 활동이 아직 생성되지 않았습니다.");

    private static async Task<int> CountAttendanceAsync(ApplicationDbContext db, int activityId) =>
        await db.Attendances.CountAsync(a => a.ActivityId == activityId);

    private static async Task<AdminTodayDto> LoadAdminDtoAsync(ApplicationDbContext db, int activityId)
    {
        var activity = await db.Activities.AsNoTracking().FirstAsync(a => a.Id == activityId);
        var attendanceCount = await CountAttendanceAsync(db, activityId);
        var totalMembers = await db.Users.CountAsync(u => u.IsActive);
        var teamCount = await db.Teams.CountAsync(t => t.ActivityId == activityId);
        return new AdminTodayDto(
            activity.Id,
            activity.ActivityDate,
            activity.Status,
            StatusText(activity.Status),
            activity.OtpCode,
            attendanceCount,
            totalMembers,
            teamCount,
            activity.AttendanceOpenedAt,
            activity.AttendanceClosedAt,
            activity.ActivityClosedAt);
    }

    internal static string StatusText(ActivityStatus status) => status switch
    {
        ActivityStatus.Ready => "준비중",
        ActivityStatus.AttendanceOpen => "🟢 출석 진행중",
        ActivityStatus.AttendanceClosed => "출석 마감",
        ActivityStatus.TeamCreated => "🏐 팀 편성 완료",
        ActivityStatus.ActivityClosed => "🔴 오늘 활동 종료",
        _ => status.ToString(),
    };
}
