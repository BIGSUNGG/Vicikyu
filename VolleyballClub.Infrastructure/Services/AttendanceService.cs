using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using VolleyballClub.Application.Abstractions;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Application.Exceptions;
using VolleyballClub.Domain;
using VolleyballClub.Domain.Entities;
using VolleyballClub.Domain.Enums;
using VolleyballClub.Infrastructure.Persistence;

namespace VolleyballClub.Infrastructure.Services;

public class AttendanceService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ICurrentUserService currentUser,
    IAuditLogService auditLog,
    ISheetFormatResolver formatResolver,
    TimeProvider timeProvider) : IAttendanceService
{
    public async Task<DateTime> CheckInWithOtpAsync(string otp)
    {
        var userId = await currentUser.RequireUserIdAsync();
        var code = otp.Trim();

        await using var db = await dbFactory.CreateDbContextAsync();
        var today = KstClock.Today(timeProvider);
        var activity = await ActivityService.FindCurrentAsync(db, today);

        if (activity.Status is not (ActivityStatus.AttendanceOpen or ActivityStatus.TeamCreated))
        {
            throw new ConflictException(activity.Status == ActivityStatus.ActivityClosed
                ? "현재 활동이 종료되었습니다."
                : "활동이 아직 시작되지 않았습니다.");
        }

        if (string.IsNullOrEmpty(activity.OtpCode) || !FixedTimeEquals(code, activity.OtpCode))
        {
            throw new RuleViolationException("OTP 번호가 올바르지 않습니다.");
        }

        // 서버 로직 선검사 + DB unique index 최종 방어(동시 요청)
        if (await db.Attendances.AnyAsync(a => a.ActivityId == activity.Id && a.UserId == userId))
        {
            throw new ConflictException("이미 출석 처리되었습니다.");
        }

        var member = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (member is null || member.Status != MemberStatus.Active)
        {
            throw new ForbiddenException("활성화된 부원만 출석할 수 있습니다.");
        }

        var now = KstClock.NowUtc(timeProvider);
        var attendance = new Attendance
        {
            ActivityId = activity.Id,
            UserId = userId,
            AttendanceType = AttendanceType.Otp,
            CheckedAt = now,
            CreatedAt = now,
        };
        db.Attendances.Add(attendance);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            throw new ConflictException("이미 출석 처리되었습니다.");
        }

        return now;
    }

    public async Task AdminAddAsync(string targetUserId)
    {
        await currentUser.RequireAdminAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var activity = await ActivityService.FindCurrentAsync(db, KstClock.Today(timeProvider));

        if (activity.Status == ActivityStatus.ActivityClosed)
        {
            throw new ConflictException("현재 활동이 종료되어 수정할 수 없습니다.");
        }

        if (await db.Attendances.AnyAsync(a => a.ActivityId == activity.Id && a.UserId == targetUserId))
        {
            throw new ConflictException("이미 출석 처리되었습니다.");
        }

        var member = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == targetUserId  && u.Status == MemberStatus.Active)
            ?? throw new NotFoundException("부원을 찾을 수 없습니다.");

        var now = KstClock.NowUtc(timeProvider);
        db.Attendances.Add(new Attendance
        {
            ActivityId = activity.Id,
            UserId = targetUserId,
            AttendanceType = AttendanceType.Admin,
            CheckedAt = now,
            CreatedAt = now,
        });
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            throw new ConflictException("이미 출석 처리되었습니다.");
        }

        await auditLog.LogAsync(AdminAction.AttendanceAdd, "User", targetUserId, $"{member.Name} 직접 출석 추가");
    }

    public async Task AdminCancelAsync(string targetUserId, bool confirmTeamImpact = false)
    {
        await currentUser.RequireAdminAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var activity = await ActivityService.FindCurrentAsync(db, KstClock.Today(timeProvider));

        if (activity.Status == ActivityStatus.ActivityClosed)
        {
            throw new ConflictException("현재 활동이 종료되어 수정할 수 없습니다.");
        }

        await RemoveAttendanceCoreAsync(db, targetUserId, activity, confirmTeamImpact);
    }

    public async Task AdminAddForActivityAsync(string targetUserId, int activityId)
    {
        await currentUser.RequireAdminAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var activity = await db.Activities.AsNoTracking().FirstOrDefaultAsync(a => a.Id == activityId)
            ?? throw new NotFoundException("활동을 찾을 수 없습니다.");

        if (activity.Status == ActivityStatus.Ready)
        {
            throw new ConflictException("아직 출석이 시작되지 않은 활동입니다.");
        }

        if (await db.Attendances.AnyAsync(a => a.ActivityId == activity.Id && a.UserId == targetUserId))
        {
            throw new ConflictException("이미 출석 처리되었습니다.");
        }

        var member = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == targetUserId)
            ?? throw new NotFoundException("부원을 찾을 수 없습니다.");

        var now = KstClock.NowUtc(timeProvider);
        db.Attendances.Add(new Attendance
        {
            ActivityId = activity.Id,
            UserId = targetUserId,
            AttendanceType = AttendanceType.Admin,
            CheckedAt = now,
            CreatedAt = now,
        });
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            throw new ConflictException("이미 출석 처리되었습니다.");
        }

        await auditLog.LogAsync(AdminAction.AttendanceAdd, "User", targetUserId,
            $"{member.Name} {activity.ActivityDate:MM.dd} 출석 추가");
    }

    public async Task AdminRemoveForActivityAsync(string targetUserId, int activityId, bool confirmTeamImpact = false)
    {
        await currentUser.RequireAdminAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var activity = await db.Activities.AsNoTracking().FirstOrDefaultAsync(a => a.Id == activityId)
            ?? throw new NotFoundException("활동을 찾을 수 없습니다.");

        await RemoveAttendanceCoreAsync(db, targetUserId, activity, confirmTeamImpact);
    }

    /// <summary>지정 활동의 출석 기록 삭제 — 팀 편성 영향 확인과 팀원 제거를 당일·과거 기록이 공유한다.</summary>
    private async Task RemoveAttendanceCoreAsync(ApplicationDbContext db, string targetUserId, Activity activity, bool confirmTeamImpact)
    {
        var attendance = await db.Attendances.FirstOrDefaultAsync(a => a.ActivityId == activity.Id && a.UserId == targetUserId)
            ?? throw new NotFoundException("출석 기록이 없습니다.");

        var hasTeams = await db.Teams.AnyAsync(t => t.ActivityId == activity.Id)
            && await db.TeamMembers.AnyAsync(m => m.UserId == targetUserId && m.Team!.ActivityId == activity.Id);
        if (hasTeams && !confirmTeamImpact)
        {
            throw new ConflictException("팀 편성이 완료되어 있습니다. 출석을 취소하면 팀에서도 제외됩니다. 계속하시겠습니까?");
        }

        var member = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == targetUserId);

        db.Attendances.Remove(attendance);
        var teamMembers = await db.TeamMembers
            .Where(m => m.UserId == targetUserId && m.Team!.ActivityId == activity.Id)
            .ToListAsync();
        db.TeamMembers.RemoveRange(teamMembers);
        await db.SaveChangesAsync();
        await auditLog.LogAsync(AdminAction.AttendanceRemove, "User", targetUserId,
            $"{member?.Name ?? targetUserId} {activity.ActivityDate:MM.dd} 출석 취소");
    }

    public async Task<MemberAttendanceAdminDto> GetAdminRecordsAsync(string userId)
    {
        await currentUser.RequireAdminAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var member = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId)
            ?? throw new NotFoundException("부원을 찾을 수 없습니다.");

        var today = KstClock.Today(timeProvider);
        var scope = await GetCurrentSemesterScopeAsync(db);
        var (semStart, semEnd) = (scope.Start, scope.End);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);

        var monthCount = await db.Attendances.AsNoTracking().CountAsync(x =>
            x.UserId == userId && x.Activity!.ActivityDate >= monthStart && x.Activity!.ActivityDate <= monthEnd);
        var semesterCount = await db.Attendances.AsNoTracking().CountAsync(x =>
            x.UserId == userId && x.Activity!.ActivityDate >= semStart && x.Activity!.ActivityDate <= semEnd);
        var totalCount = await db.Attendances.AsNoTracking().CountAsync(x => x.UserId == userId);

        var records = await db.Attendances.AsNoTracking()
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.Activity!.ActivityDate)
            .ThenByDescending(x => x.Id)
            .Select(x => new { x.Id, x.ActivityId, x.Activity!.ActivityDate, x.AttendanceType, x.CheckedAt })
            .ToListAsync();
        var teamActivityIds = (await db.TeamMembers.AsNoTracking()
                .Where(m => m.UserId == userId)
                .Select(m => m.Team!.ActivityId)
                .Distinct()
                .ToListAsync())
            .ToHashSet();

        // 출석 추가 후보 — 현재 학기의 시작된 활동(이미 출석한 활동은 화면에서 비활성 표시)
        var attendedIds = records.Select(x => x.ActivityId).ToHashSet();
        var candidates = await db.Activities.AsNoTracking()
            .Where(a => a.ActivityDate >= semStart && a.ActivityDate <= semEnd && a.Status != ActivityStatus.Ready)
            .OrderByDescending(a => a.ActivityDate)
            .ThenByDescending(a => a.Id)
            .Select(a => new { a.Id, a.ActivityDate, a.Status })
            .ToListAsync();

        return new MemberAttendanceAdminDto(
            userId,
            member.Name,
            monthCount,
            semesterCount,
            totalCount,
            scope,
            records.Select(x => new MemberAttendanceRecordDto(
                x.Id, x.ActivityId, x.ActivityDate, x.AttendanceType, x.CheckedAt, teamActivityIds.Contains(x.ActivityId)))
                .ToList(),
            candidates.Select(a => new ActivityOptionDto(a.Id, a.ActivityDate, a.Status, attendedIds.Contains(a.Id)))
                .ToList());
    }

    public async Task<List<AttendanceMemberDto>> GetTodayMembersAsync(string? search = null)
    {
        await currentUser.RequireAdminAsync();
        var today = KstClock.Today(timeProvider);
        await using var db = await dbFactory.CreateDbContextAsync();
        var currentActivityId = (await ActivityService.FindCurrentOrDefaultAsync(db, today))?.Id ?? 0;

        var query =
            from m in db.Users.AsNoTracking()
            join a in db.Attendances.AsNoTracking()
                .Where(x => x.ActivityId == currentActivityId)
                on m.Id equals a.UserId into ga
            from a in ga.DefaultIfEmpty()
            where m.Status == MemberStatus.Active
            select new AttendanceMemberDto(
                m.Id,
                m.Name,
                m.StudentNumber,
                m.Department,
                a != null,
                a != null ? (DateTime?)a.CheckedAt : null,
                a != null ? (AttendanceType?)a.AttendanceType : null,
                a != null ? a.Id : 0);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(x => x.Name.ToLower().Contains(term) || x.StudentNumber.ToLower().Contains(term));
        }

        var list = await query.ToListAsync();
        return list
            .OrderByDescending(x => x.IsAttended)
            .ThenBy(x => x.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    public async Task<MyCalendarDto> GetMyCalendarAsync(string userId, int year, int month)
    {
        var first = new DateOnly(year, month, 1);
        var last = first.AddMonths(1).AddDays(-1);
        await using var db = await dbFactory.CreateDbContextAsync();

        var myAttendances = await db.Attendances.AsNoTracking()
            .Where(x => x.UserId == userId && x.Activity!.ActivityDate >= first && x.Activity!.ActivityDate <= last)
            .Select(x => new { x.Activity!.ActivityDate, x.CheckedAt })
            .ToListAsync();

        var activityIdsByDate = await db.Activities.AsNoTracking()
            .Where(a => a.ActivityDate >= first && a.ActivityDate <= last)
            .Select(a => new { a.Id, a.ActivityDate, a.Status, a.CreatedByUserId })
            .ToListAsync();

        // 같은 날 여러 세션이면 하루 치로 병합한다(BuildWeeks의 날짜 딕셔너리 중복 방지)
        var days = myAttendances
            .GroupBy(x => x.ActivityDate)
            .Select(g => new CalendarDayDto(g.Key, CalendarDayKind.Attended, g.OrderByDescending(x => x.CheckedAt).First().CheckedAt))
            .ToList();

        // 마감된 활동 중 미출석일만 결석으로 표시 — 마감 전 활동은 진행 중이므로 제외,
        // 같은 날 활동이 여러 개여도 하루 치만 표시한다(BuildWeeks의 날짜 딕셔너리 중복 방지).
        // 시트 가져오기 활동은 그 학기 탭에 매핑된 부원만 결석으로 표시한다(비회원 학기 제외)
        var attendedDates = myAttendances.Select(x => x.ActivityDate).ToHashSet();
        var mappedPeriods = await GetMappedPeriodsAsync(db, userId);
        days.AddRange(activityIdsByDate
            .Where(a => a.Status == ActivityStatus.ActivityClosed
                && !attendedDates.Contains(a.ActivityDate)
                && CountsForUser(a.CreatedByUserId, a.ActivityDate, mappedPeriods))
            .GroupBy(a => a.ActivityDate)
            .Select(g => new CalendarDayDto(g.Key, CalendarDayKind.Missed, null)));

        // 시트의 예정/없음 날징은 캘린더에 상태로 표시한다 — 출석·미출석 표시가 이미 있는 날은 우선한다
        var markedDates = days.Select(d => d.Date).ToHashSet();
        var dayMarks = await db.SheetDayMarks.AsNoTracking()
            .Where(m => m.Date >= first && m.Date <= last)
            .ToListAsync();
        days.AddRange(dayMarks
            .Where(m => !markedDates.Contains(m.Date))
            .Select(m => new CalendarDayDto(
                m.Date,
                m.Kind == SheetDayKind.Scheduled ? CalendarDayKind.Scheduled : CalendarDayKind.NoActivity,
                null)));

        var scope = await GetCurrentSemesterScopeAsync(db);
        var monthCount = myAttendances.Count;
        var semesterCount = await db.Attendances.AsNoTracking()
            .CountAsync(x => x.UserId == userId && x.Activity!.ActivityDate >= scope.Start && x.Activity!.ActivityDate <= scope.End);
        var totalCount = await db.Attendances.AsNoTracking()
            .CountAsync(x => x.UserId == userId);

        return new MyCalendarDto(year, month, days, monthCount, semesterCount, totalCount, scope);
    }

    /// <summary>이 부원이 시트 탭에 매핑된 학기 기간 목록 — 매핑은 곧 '그 학기에 부원이었다'의 판별 근거.</summary>
    private async Task<List<(DateOnly From, DateOnly To)>> GetMappedPeriodsAsync(ApplicationDbContext db, string userId)
    {
        var tabNames = await db.SheetMemberMappings.AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => m.TabName)
            .ToListAsync();

        return tabNames
            .Select(formatResolver.ResolveByTabName)
            .Where(format => format is not null)
            .Select(format => (format!.From, format.To))
            .ToList();
    }

    /// <summary>미출석 계산 대상 활동인지 — 앱 활동은 항상 대상,
    /// 시트 가져오기 활동은 그 학기 탭에 매핑된 부원만 대상(비회원 학기의 과거 활동은 미출석에서 제외).</summary>
    private static bool CountsForUser(string createdByUserId, DateOnly activityDate, List<(DateOnly From, DateOnly To)> mappedPeriods) =>
        createdByUserId != Activity.SheetImportCreator
        || mappedPeriods.Any(p => activityDate >= p.From && activityDate <= p.To);

    /// <summary>현재 학기 범위 DTO — 학기 행이 없으면 기본 규칙 범위에 Name=null.</summary>
    private async Task<SemesterScopeDto> GetCurrentSemesterScopeAsync(ApplicationDbContext db)
    {
        var today = KstClock.Today(timeProvider);
        var period = await SemesterPeriodResolver.GetCurrentAsync(db, today);
        if (period is not null)
        {
            return new SemesterScopeDto(period.Name, period.StartDate, period.EndDate);
        }

        var (start, end) = SemesterPeriodResolver.DefaultRange(today);
        return new SemesterScopeDto(null, start, end);
    }

    public async Task<MyRecordsDto> GetMyRecordsAsync(string userId, int year, int month)
    {
        var first = new DateOnly(year, month, 1);
        var last = first.AddMonths(1).AddDays(-1);
        await using var db = await dbFactory.CreateDbContextAsync();

        var activities = await db.Activities.AsNoTracking()
            .Where(a => a.ActivityDate >= first && a.ActivityDate <= last)
            .Select(a => new { a.Id, a.ActivityDate, a.Status, a.CreatedByUserId })
            .ToListAsync();

        var myAttendances = await db.Attendances.AsNoTracking()
            .Where(x => x.UserId == userId && x.Activity!.ActivityDate >= first && x.Activity!.ActivityDate <= last)
            .Select(x => new { x.ActivityId, x.CheckedAt })
            .ToListAsync();

        // 시트 가져오기 활동 중 이 부원이 매핑되지 않은 학기는 비회원 기간 — 미출석으로 표시하지 않는다
        var attendedIds = myAttendances.Select(x => x.ActivityId).ToHashSet();
        var mappedPeriods = await GetMappedPeriodsAsync(db, userId);
        var visible = activities
            .Where(a => attendedIds.Contains(a.Id) || CountsForUser(a.CreatedByUserId, a.ActivityDate, mappedPeriods))
            .ToList();

        var records = visible
            .OrderByDescending(a => a.ActivityDate)
            .ThenByDescending(a => a.Id) // 같은 날 여러 세션은 최신 세션 우선
            .Select(a =>
            {
                var attendance = myAttendances.FirstOrDefault(x => x.ActivityId == a.Id);
                return new ActivityRecordDto(
                    a.Id,
                    a.ActivityDate,
                    a.Status,
                    attendance is not null,
                    attendance?.CheckedAt);
            })
            .ToList();

        return new MyRecordsDto(year, month, records, myAttendances.Count, visible.Count);
    }

    public async Task<MySemesterStatsDto> GetMySemesterStatsAsync(string userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var scope = await GetCurrentSemesterScopeAsync(db);
        var (semStart, semEnd) = (scope.Start, scope.End);

        // 실제 개최된 활동(출석이 한 번이라도 열림)만 분모.
        // 시트 가져오기 활동 중 이 부원이 매핑되지 않은 학기는 비회원 기간 — 분모에서도 제외한다
        var semesterActivities = await db.Activities.AsNoTracking()
            .Where(a => a.ActivityDate >= semStart && a.ActivityDate <= semEnd && a.Status != ActivityStatus.Ready)
            .Select(a => new { a.Id, a.ActivityDate, a.CreatedByUserId })
            .ToListAsync();
        var mappedPeriods = await GetMappedPeriodsAsync(db, userId);
        var countedActivities = semesterActivities
            .Where(a => CountsForUser(a.CreatedByUserId, a.ActivityDate, mappedPeriods))
            .Select(a => a.Id)
            .ToList();

        var myAttendances = await db.Attendances.AsNoTracking()
            .CountAsync(x => x.UserId == userId && countedActivities.Contains(x.ActivityId));

        var totalActiveMembers = await db.Users.CountAsync(u => u.Status == MemberStatus.Active);

        var counts = await db.Attendances.AsNoTracking()
            .Where(x => countedActivities.Contains(x.ActivityId))
            .GroupBy(x => x.UserId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToListAsync();

        var myCount = counts.FirstOrDefault(c => c.UserId == userId)?.Count ?? 0;
        var rank = counts.Count(c => c.Count > myCount) + 1;

        var rate = countedActivities.Count == 0 ? 0 : (int)Math.Round(myAttendances * 100.0 / countedActivities.Count);
        return new MySemesterStatsDto(countedActivities.Count, myAttendances, rate, rank, totalActiveMembers, scope);
    }

    public async Task<List<AttendanceRankingDto>> GetRankingAsync(RankingPeriod period, string? viewerUserId = null)
    {
        var today = KstClock.Today(timeProvider);
        await using var db = await dbFactory.CreateDbContextAsync();
        var (start, end) = period switch
        {
            RankingPeriod.Semester => await SemesterPeriodResolver.GetCurrentRangeAsync(db, today),
            RankingPeriod.Month => (new DateOnly(today.Year, today.Month, 1), new DateOnly(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month))),
            _ => (DateOnly.MinValue, DateOnly.MaxValue),
        };

        var counts = await (
            from a in db.Attendances.AsNoTracking()
            where a.Activity!.ActivityDate >= start && a.Activity!.ActivityDate <= end
                && a.Activity.Status != ActivityStatus.Ready
            join u in db.Users.AsNoTracking() on a.UserId equals u.Id
            where u.Status == MemberStatus.Active
            group a by new { u.Id, u.Name, u.Department } into g
            select new { g.Key.Id, g.Key.Name, g.Key.Department, Count = g.Count() }
        ).ToListAsync();

        var sorted = counts.OrderByDescending(x => x.Count).ThenBy(x => x.Name).ToList();

        var result = new List<AttendanceRankingDto>();
        var currentRank = 0;
        var previousCount = -1;
        foreach (var (index, item) in sorted.Select((x, i) => (i, x)))
        {
            currentRank = item.Count == previousCount ? currentRank : index + 1;
            previousCount = item.Count;
            result.Add(new AttendanceRankingDto(currentRank, item.Id, item.Name, item.Department, item.Count, item.Id == viewerUserId));
        }

        return result;
    }

    /// <summary>OTP 비교는 타이밍 차이 노출을 막기 위해 상수 시간 비교를 사용한다.</summary>
    private static bool FixedTimeEquals(string a, string b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(a),
            System.Text.Encoding.ASCII.GetBytes(b));
    }
}
