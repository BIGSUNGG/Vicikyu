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
    TimeProvider timeProvider) : IActivityService
{
    public async Task<AdminTodayDto> GetTodayForAdminAsync()
    {
        await currentUser.RequireAdminAsync();
        var today = KstClock.Today(timeProvider);
        await using var db = await dbFactory.CreateDbContextAsync();
        await AdminActivityService.SweepStaleScheduledAsync(db, today, timeProvider);
        var activity = await FindCurrentOrDefaultAsync(db, today);
        if (activity is not null) return await LoadAdminDtoAsync(db, activity.Id);

        // 시작 전 — 조회만으로 행을 만들지 않고 가상 '준비 중' 상태를 반환한다
        var totalMembers = await db.Users.CountAsync(u => u.Status == MemberStatus.Active);
        return new AdminTodayDto(0, today, null, "준비 중", null, 0, totalMembers, 0, 0, null, null, null);
    }

    public async Task<MyTodayDto?> GetMyTodayAsync(string userId)
    {
        var today = KstClock.Today(timeProvider);
        await using var db = await dbFactory.CreateDbContextAsync();
        await AdminActivityService.SweepStaleScheduledAsync(db, today, timeProvider);
        var activity = await FindCurrentOrDefaultAsync(db, today);

        if (activity is null)
        {
            // 시작 전 — 오늘 예정 표시가 있으면 가상 '활동 예정', 그 외엔 일정 없음
            var scheduled = await db.DayMarks.AsNoTracking().AnyAsync(m => m.Date == today && m.Kind == DayKind.Scheduled);
            if (!scheduled) return null;
            var members = await db.Users.CountAsync(u => u.Status == MemberStatus.Active);
            return new MyTodayDto(0, today, null, "활동 예정", false, null, null, 0, members);
        }

        var attendance = await db.SemesterAttendances.AsNoTracking()
            .Where(a => a.ActivityId == activity.Id && a.SemesterMember.MappedUserId == userId)
            .Select(a => new { a.CheckedAt, a.AttendanceType })
            .FirstOrDefaultAsync();

        var attendanceCount = await CountAttendanceAsync(db, activity.Id);
        var totalMembers = await db.Users.CountAsync(u => u.Status == MemberStatus.Active);

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
        await AdminActivityService.SweepStaleScheduledAsync(db, today, timeProvider);
        var activity = await FindCurrentAsync(db, today);

        activity.OtpCode = GenerateOtpCode();
        activity.UpdatedAt = KstClock.NowUtc(timeProvider);
        await db.SaveChangesAsync();
        await auditLog.LogAsync(AdminAction.OtpChange, "Activity", activity.Id.ToString(), "당일 OTP 생성/변경");
        return activity.OtpCode;
    }

    /// <summary>OTP 숫자 코드 생성 — 4자리 고정.</summary>
    private static string GenerateOtpCode()
    {
        return RandomNumberGenerator.GetInt32(0, 10000).ToString().PadLeft(4, '0');
    }

    public async Task<AdminTodayDto> StartNewActivityAsync()
    {
        await currentUser.RequireAdminAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var today = KstClock.Today(timeProvider);
        await AdminActivityService.SweepStaleScheduledAsync(db, today, timeProvider);
        var current = await FindCurrentOrDefaultAsync(db, today);
        if (current is not null && current.Status != ActivityStatus.ActivityClosed)
        {
            throw new ConflictException("진행 중인 활동이 있습니다. 먼저 마감해 주세요.");
        }

        var adminId = await currentUser.RequireUserIdAsync();
        var now = KstClock.NowUtc(timeProvider);
        var activity = new Activity
        {
            ActivityDate = today,
            Status = ActivityStatus.AttendanceOpen,
            OtpCode = GenerateOtpCode(),
            AttendanceOpenedAt = now,
            CreatedByUserId = adminId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Activities.Add(activity);
        // 예정 표시는 실제 활동이 대체한다(예정일 시작 시 같은 날 중복 방지)
        db.DayMarks.RemoveRange(db.DayMarks.Where(m => m.Date == today));
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // 진행 중 활동 하루 1개 부분 유니크 인덱스 최종 방어(동시 클릭)
            throw new ConflictException("동시에 새 활동이 시작되었습니다. 새로고침 후 확인해 주세요.");
        }
        await auditLog.LogAsync(AdminAction.ActivityCreate, "Activity", activity.Id.ToString(), $"{today:yyyy-MM-dd} 새 세션 활동 시작");

        return await LoadAdminDtoAsync(db, activity.Id);
    }

    public async Task CancelActivityAsync()
    {
        await currentUser.RequireAdminAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var today = KstClock.Today(timeProvider);
        await AdminActivityService.SweepStaleScheduledAsync(db, today, timeProvider);
        var activity = await FindCurrentAsync(db, today);
        if (!activity.CanCancel)
        {
            throw new ConflictException("종료된 활동은 취소할 수 없습니다. '활동 다시 열기' 후 취소해 주세요.");
        }

        var attendanceCount = await db.SemesterAttendances.CountAsync(a => a.ActivityId == activity.Id);
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
        var today = KstClock.Today(timeProvider);
        await AdminActivityService.SweepStaleScheduledAsync(db, today, timeProvider);
        var activity = await FindCurrentAsync(db, today);
        if (!activity.CanClose) throw new ConflictException("이미 종료된 활동입니다.");
        var now = KstClock.NowUtc(timeProvider);
        var teams = await db.Teams.Where(t => t.ActivityId == activity.Id).ToListAsync();
        var teamCount = teams.Count;
        db.Teams.RemoveRange(teams);
        activity.Status = ActivityStatus.ActivityClosed;
        activity.ActivityClosedAt = now;
        activity.AttendanceOpenedAt ??= now;
        activity.AttendanceClosedAt ??= now;
        activity.UpdatedAt = now;
        await db.SaveChangesAsync();
        await auditLog.LogAsync(AdminAction.ActivityClose, "Activity", activity.Id.ToString(),
            $"오늘 활동 마감{(teamCount > 0 ? $" (팀 {teamCount}개 삭제)" : string.Empty)}");

        return await LoadAdminDtoAsync(db, activity.Id);
    }

    public async Task<AdminTodayDto> ReopenActivityAsync()
    {
        await currentUser.RequireAdminAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var today = KstClock.Today(timeProvider);
        await AdminActivityService.SweepStaleScheduledAsync(db, today, timeProvider);
        var activity = await FindCurrentAsync(db, today);
        if (!activity.CanReopen)
        {
            throw new ConflictException("종료된 활동만 다시 열 수 있습니다.");
        }

        var now = KstClock.NowUtc(timeProvider);
        // 마감 시 팀이 삭제되므로 항상 출석 상태로 복귀한다
        activity.Status = ActivityStatus.AttendanceOpen;
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
        ?? await db.Activities
            .Where(a => a.ActivityDate == today)
            .OrderByDescending(a => a.Id) // 같은 날 마감 세션 여러 개면 최신 것
            .FirstOrDefaultAsync();

    internal static async Task<Activity> FindCurrentAsync(ApplicationDbContext db, DateOnly today) =>
        await FindCurrentOrDefaultAsync(db, today)
        ?? await db.Activities.OrderByDescending(a => a.Id).FirstOrDefaultAsync() // 마감 직후(자정 넘김 포함)에도 종료 안내·거부·되돌리기 가능하게
        ?? throw new NotFoundException("현재 활동이 아직 생성되지 않았습니다.");

    private static async Task<int> CountAttendanceAsync(ApplicationDbContext db, int activityId) =>
        await db.SemesterAttendances.CountAsync(a => a.ActivityId == activityId);

    private static async Task<AdminTodayDto> LoadAdminDtoAsync(ApplicationDbContext db, int activityId)
    {
        var activity = await db.Activities.AsNoTracking().FirstAsync(a => a.Id == activityId);
        var attendanceCount = await CountAttendanceAsync(db, activityId);
        var totalMembers = await db.Users.CountAsync(u => u.Status == MemberStatus.Active);
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
        ActivityStatus.AttendanceOpen => "활동 중",
        ActivityStatus.AttendanceClosed => "출석 마감",
        ActivityStatus.TeamCreated => "팀 편성 완료",
        ActivityStatus.ActivityClosed => "오늘 활동 종료",
        _ => status.ToString(),
    };
}
