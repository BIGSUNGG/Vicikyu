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
        // 진행 중(미종료) 활동이 있으면 날짜와 무관하게 그것을 계속 사용한다(자정 넘김 대응)
        var activity = await FindCurrentOrDefaultAsync(db, today);
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
        var activity = await FindCurrentAsync(db, today);
        return await LoadAdminDtoAsync(db, activity.Id);
    }

    public async Task<MyTodayDto> GetMyTodayAsync(string userId)
    {
        var today = KstClock.Today(timeProvider);
        await using var db = await dbFactory.CreateDbContextAsync();
        var activity = await FindCurrentAsync(db, today);

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
        var activity = await FindCurrentAsync(db, today);

        var length = Math.Clamp(await settings.GetIntAsync("OtpLength", 4), 4, 8);
        var max = (int)Math.Pow(10, length);
        var code = RandomNumberGenerator.GetInt32(0, max).ToString().PadLeft(length, '0');

        activity.OtpCode = code;
        activity.UpdatedAt = KstClock.NowUtc(timeProvider);
        await db.SaveChangesAsync();
        await auditLog.LogAsync(AdminAction.OtpChange, "Activity", activity.Id.ToString(), "당일 OTP 생성/변경");
        return code;
    }

    public async Task<AdminTodayDto> StartActivityAsync()
    {
        await currentUser.RequireAdminAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var activity = await FindCurrentAsync(db, KstClock.Today(timeProvider));
        if (!activity.CanStartAttendance)
        {
            throw new ConflictException("이미 활동이 시작되었습니다.");
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
        await auditLog.LogAsync(AdminAction.AttendanceOpen, "Activity", activity.Id.ToString(), "활동 시작");

        return await LoadAdminDtoAsync(db, activity.Id);
    }

    public async Task CancelActivityAsync()
    {
        await currentUser.RequireAdminAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var activity = await FindCurrentAsync(db, KstClock.Today(timeProvider));
        if (!activity.CanCancel)
        {
            throw new ConflictException("종료된 활동은 취소할 수 없습니다. '활동 다시 열기' 후 취소해 주세요.");
        }

        var attendanceCount = await db.Attendances.CountAsync(a => a.ActivityId == activity.Id);
        var guestCount = await db.Guests.CountAsync(g => g.ActivityId == activity.Id);
        var teamCount = await db.Teams.CountAsync(t => t.ActivityId == activity.Id);
        db.Activities.Remove(activity);
        await db.SaveChangesAsync();
        await auditLog.LogAsync(AdminAction.ActivityCancel, "Activity", activity.Id.ToString(),
            $"{activity.ActivityDate:yyyy-MM-dd} 활동 취소 (출석 {attendanceCount}·게스트 {guestCount}·팀 {teamCount} 삭제)");
    }

    public async Task<AdminTodayDto> CloseActivityAsync()
    {
        await currentUser.RequireAdminAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var activity = await FindCurrentAsync(db, KstClock.Today(timeProvider));
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
        var activity = await FindCurrentAsync(db, KstClock.Today(timeProvider));
        if (!activity.CanReopen)
        {
            throw new ConflictException("종료된 활동만 다시 열 수 있습니다.");
        }

        var now = KstClock.NowUtc(timeProvider);
        // 팀 편성 데이터가 있으면 TeamCreated, 없으면 AttendanceOpen 상태로 되돌린다
        var hasTeams = await db.Teams.AnyAsync(t => t.ActivityId == activity.Id);
        activity.Status = hasTeams ? ActivityStatus.TeamCreated : ActivityStatus.AttendanceOpen;
        activity.ActivityClosedAt = null;
        activity.UpdatedAt = now;
        await db.SaveChangesAsync();
        await auditLog.LogAsync(AdminAction.ActivityReopen, "Activity", activity.Id.ToString(), "활동 다시 열기");

        return await LoadAdminDtoAsync(db, activity.Id);
    }

    /// <summary>현재 활동: 진행 중(미종료) 활동이 있으면 가장 최근 것, 없으면 오늘 날짜 활동.
    /// 활동 종료는 관리자 마감으로만 결정되며 날짜 경과로 자동 종료되지 않는다.</summary>
    internal static async Task<Activity?> FindCurrentOrDefaultAsync(ApplicationDbContext db, DateOnly today) =>
        await db.Activities
            .Where(a => a.Status != ActivityStatus.ActivityClosed)
            .OrderByDescending(a => a.Id)
            .FirstOrDefaultAsync()
        ?? await db.Activities.FirstOrDefaultAsync(a => a.ActivityDate == today);

    internal static async Task<Activity> FindCurrentAsync(ApplicationDbContext db, DateOnly today) =>
        await FindCurrentOrDefaultAsync(db, today)
        ?? await db.Activities.OrderByDescending(a => a.Id).FirstOrDefaultAsync() // 마감 직후에도 종료 안내/되돌리기 가능하게
        ?? throw new NotFoundException("현재 활동이 아직 생성되지 않았습니다.");

    private static async Task<int> CountAttendanceAsync(ApplicationDbContext db, int activityId) =>
        await db.Attendances.CountAsync(a => a.ActivityId == activityId);

    private static async Task<AdminTodayDto> LoadAdminDtoAsync(ApplicationDbContext db, int activityId)
    {
        var activity = await db.Activities.AsNoTracking().FirstAsync(a => a.Id == activityId);
        var attendanceCount = await CountAttendanceAsync(db, activityId);
        var totalMembers = await db.Users.CountAsync(u => u.IsActive);
        var teamCount = await db.Teams.CountAsync(t => t.ActivityId == activityId);
        var guestCount = await db.Guests.CountAsync(g => g.ActivityId == activityId);
        return new AdminTodayDto(
            activity.Id,
            activity.ActivityDate,
            activity.Status,
            StatusText(activity.Status),
            activity.OtpCode,
            attendanceCount,
            totalMembers,
            teamCount,
            guestCount,
            activity.AttendanceOpenedAt,
            activity.AttendanceClosedAt,
            activity.ActivityClosedAt);
    }

    internal static string StatusText(ActivityStatus status) => status switch
    {
        ActivityStatus.Ready => "준비중",
        ActivityStatus.AttendanceOpen => "활동 중",
        ActivityStatus.AttendanceClosed => "출석 마감",
        ActivityStatus.TeamCreated => "팀 편성 완료",
        ActivityStatus.ActivityClosed => "오늘 활동 종료",
        _ => status.ToString(),
    };
}
