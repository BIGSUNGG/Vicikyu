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
    ISheetSyncService sheetSync,
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

    public async Task<AdminTodayDto> StartActivityAsync()
    {
        await currentUser.RequireAdminAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        Activity activity;
        await ManagedSheetEdits.AttendanceCommitGate.WaitAsync();
        try
        {
            activity = await FindCurrentAsync(db, KstClock.Today(timeProvider));
            if (!activity.CanStartAttendance) throw new ConflictException("이미 활동이 시작되었습니다.");
            if (string.IsNullOrEmpty(activity.OtpCode)) activity.OtpCode = GenerateOtpCode();
            var now = KstClock.NowUtc(timeProvider);
            activity.Status = ActivityStatus.AttendanceOpen;
            activity.AttendanceOpenedAt = now;
            activity.UpdatedAt = now;
            activity.MarkSheetPending(now);
            await db.SaveChangesAsync();
        }
        finally { ManagedSheetEdits.AttendanceCommitGate.Release(); }
        await auditLog.LogAsync(AdminAction.AttendanceOpen, "Activity", activity.Id.ToString(), "활동 시작");

        // 신형 탭 상태행에 활동 예정 기록(구형 탭은 기록 없음. 조회 경로 자동생성은 기록 안 함).
        // 실패해도 시작은 유효 — 미반영으로 보존되고 관리자 화면에 알림이 뜬다.
        await sheetSync.ExportScheduledActivityAsync(activity.Id);

        return await LoadAdminDtoAsync(db, activity.Id);
    }

    public async Task<AdminTodayDto> StartNewActivityAsync()
    {
        await currentUser.RequireAdminAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var today = KstClock.Today(timeProvider);
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
        activity.MarkSheetPending(now);
        db.Activities.Add(activity);
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

        // 신형 탭 상태행에 활동 예정 기록(구형 탭은 기록 없음). 실패해도 시작은 유효 — 미반영으로 보존된다.
        await sheetSync.ExportScheduledActivityAsync(activity.Id);

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

        // 마감으로 시트에 기록했던 출석 정보를 먼저 지운다(완료→없음 + 마크 공란) — 삭제 후엔 날짜를 알 수 없다.
        // best-effort: 실패해도 취소는 진행한다(남은 시트 표기는 수기 정리 대상).
        await sheetSync.RemoveCancelledActivityAsync(activity.Id);

        db.Activities.Remove(activity);
        await db.SaveChangesAsync();
        await auditLog.LogAsync(AdminAction.ActivityCancel, "Activity", activity.Id.ToString(),
            $"{activity.ActivityDate:yyyy-MM-dd} 활동 취소 (출석 {attendanceCount}·게스트 {guestCount}·팀 {teamCount} 삭제)");
    }

    public async Task<AdminTodayDto> CloseActivityAsync()
    {
        await currentUser.RequireAdminAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        Activity activity;
        var teamCount = 0;
        await ManagedSheetEdits.AttendanceCommitGate.WaitAsync();
        try
        {
            activity = await FindCurrentAsync(db, KstClock.Today(timeProvider));
            if (!activity.CanClose) throw new ConflictException("이미 종료된 활동입니다.");
            var now = KstClock.NowUtc(timeProvider);
            var teams = await db.Teams.Where(t => t.ActivityId == activity.Id).ToListAsync();
            teamCount = teams.Count;
            db.Teams.RemoveRange(teams);
            activity.Status = ActivityStatus.ActivityClosed;
            activity.ActivityClosedAt = now;
            activity.AttendanceOpenedAt ??= now;
            activity.AttendanceClosedAt ??= now;
            activity.UpdatedAt = now;
            activity.MarkSheetPending(now);
            await db.SaveChangesAsync();
        }
        finally { ManagedSheetEdits.AttendanceCommitGate.Release(); }
        await auditLog.LogAsync(AdminAction.ActivityClose, "Activity", activity.Id.ToString(),
            $"오늘 활동 마감{(teamCount > 0 ? $" (팀 {teamCount}개 삭제)" : string.Empty)}");

        // 시트 출석부 내보내기 — 실패해도 마감은 유지한다(미반영으로 보존, 시트 갱신 시 재시도)
        if (!await sheetSync.ExportClosedActivityAsync(activity.Id))
        {
            await auditLog.LogAsync(AdminAction.ActivityClose, "Activity", activity.Id.ToString(),
                "시트 내보내기 실패 — 미반영 상태로 보존, 시트 갱신 시 재시도");
        }

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
        // 마감 시 팀이 삭제되므로 항상 출석 상태로 복귀한다
        activity.Status = ActivityStatus.AttendanceOpen;
        activity.ActivityClosedAt = null;
        // 다시 열면 시트 재기록 대상으로 — 재마감 시 같은 날짜 열을 제자리 덮어쓰므로 중복 열이 생기지 않는다
        activity.SheetExportedAt = null;
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
        ?? await db.Activities.OrderByDescending(a => a.Id).FirstOrDefaultAsync() // 마감 직후에도 종료 안내/되돌리기 가능하게
        ?? throw new NotFoundException("현재 활동이 아직 생성되지 않았습니다.");

    private static async Task<int> CountAttendanceAsync(ApplicationDbContext db, int activityId) =>
        await db.Attendances.CountAsync(a => a.ActivityId == activityId);

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
        ActivityStatus.Ready => "준비중",
        ActivityStatus.AttendanceOpen => "활동 중",
        ActivityStatus.AttendanceClosed => "출석 마감",
        ActivityStatus.TeamCreated => "팀 편성 완료",
        ActivityStatus.ActivityClosed => "오늘 활동 종료",
        _ => status.ToString(),
    };
}
