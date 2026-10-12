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
    TimeProvider timeProvider) : IAttendanceService
{
    public async Task<DateTime> CheckInWithOtpAsync(string otp)
    {
        var userId = await currentUser.RequireUserIdAsync();
        var code = otp.Trim();

        await using var db = await dbFactory.CreateDbContextAsync();
        var today = KstClock.Today(timeProvider);
        var activity = await ActivityService.FindTodayAsync(db, today);

        if (activity.Status is not (ActivityStatus.AttendanceOpen or ActivityStatus.TeamCreated))
        {
            throw new ConflictException(activity.Status == ActivityStatus.ActivityClosed
                ? "오늘 활동이 종료되었습니다."
                : "오늘 활동이 아직 시작되지 않았습니다.");
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
        if (member is null || !member.IsActive)
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
        var activity = await ActivityService.FindTodayAsync(db, KstClock.Today(timeProvider));

        if (activity.Status == ActivityStatus.ActivityClosed)
        {
            throw new ConflictException("현재 활동이 종료되어 수정할 수 없습니다.");
        }

        if (await db.Attendances.AnyAsync(a => a.ActivityId == activity.Id && a.UserId == targetUserId))
        {
            throw new ConflictException("이미 출석 처리되었습니다.");
        }

        var member = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == targetUserId && u.IsActive)
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
        var activity = await ActivityService.FindTodayAsync(db, KstClock.Today(timeProvider));

        if (activity.Status == ActivityStatus.ActivityClosed)
        {
            throw new ConflictException("현재 활동이 종료되어 수정할 수 없습니다.");
        }

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
        await auditLog.LogAsync(AdminAction.AttendanceRemove, "User", targetUserId, $"{member?.Name ?? targetUserId} 출석 취소");
    }

    public async Task<List<AttendanceMemberDto>> GetTodayMembersAsync(string? search = null)
    {
        await currentUser.RequireAdminAsync();
        var today = KstClock.Today(timeProvider);
        await using var db = await dbFactory.CreateDbContextAsync();

        var query =
            from m in db.Users.AsNoTracking()
            join a in db.Attendances.AsNoTracking()
                .Where(x => x.Activity!.ActivityDate == today)
                on m.Id equals a.UserId into ga
            from a in ga.DefaultIfEmpty()
            where m.IsActive
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

        var myTeams = await db.TeamMembers.AsNoTracking()
            .Where(m => m.UserId == userId && m.Team!.Activity!.ActivityDate >= first && m.Team!.Activity!.ActivityDate <= last
                && m.Team!.Activity!.ConfirmedDrawNumber != null && m.Team!.DrawNumber <= m.Team!.Activity!.ConfirmedDrawNumber)
            .Select(m => new { m.Team!.ActivityId, Draw = m.Team!.DrawNumber, m.Team.Name })
            .ToListAsync();

        var activityIdsByDate = await db.Activities.AsNoTracking()
            .Where(a => a.ActivityDate >= first && a.ActivityDate <= last)
            .Select(a => new { a.Id, a.ActivityDate, a.Status })
            .ToListAsync();

        var days = myAttendances.Select(x =>
        {
            var activityId = activityIdsByDate.FirstOrDefault(a => a.ActivityDate == x.ActivityDate)?.Id;
            var teams = myTeams
                .Where(t => t.ActivityId == activityId)
                .OrderBy(t => t.Draw)
                .Select(t => new TeamAssignmentDto(t.Draw, t.Name))
                .ToList();
            return new CalendarDayDto(x.ActivityDate, true, x.CheckedAt, teams);
        }).ToList();

        // 개최된 활동(Ready 제외) 중 미출석일 — 오늘 출석 중인 활동은 아직 결석이 아니므로 제외,
        // 같은 날 활동이 여러 개여도 하루 치만 표시한다(BuildWeeks의 날짜 딕셔너리 중복 방지)
        var today = KstClock.Today(timeProvider);
        var attendedDates = myAttendances.Select(x => x.ActivityDate).ToHashSet();
        days.AddRange(activityIdsByDate
            .Where(a => a.Status != ActivityStatus.Ready && !attendedDates.Contains(a.ActivityDate)
                && !(a.ActivityDate == today && a.Status == ActivityStatus.AttendanceOpen))
            .GroupBy(a => a.ActivityDate)
            .Select(g => new CalendarDayDto(g.Key, false, null, [], IsMissed: true)));

        var scope = await GetCurrentSemesterScopeAsync(db);
        var monthCount = myAttendances.Count;
        var semesterCount = await db.Attendances.AsNoTracking()
            .CountAsync(x => x.UserId == userId && x.Activity!.ActivityDate >= scope.Start && x.Activity!.ActivityDate <= scope.End);
        var totalCount = await db.Attendances.AsNoTracking()
            .CountAsync(x => x.UserId == userId);

        return new MyCalendarDto(year, month, days, monthCount, semesterCount, totalCount, scope);
    }

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
            .Select(a => new { a.Id, a.ActivityDate, a.Status })
            .ToListAsync();

        var myAttendances = await db.Attendances.AsNoTracking()
            .Where(x => x.UserId == userId && x.Activity!.ActivityDate >= first && x.Activity!.ActivityDate <= last)
            .Select(x => new { x.ActivityId, x.CheckedAt })
            .ToListAsync();

        var myTeams = await db.TeamMembers.AsNoTracking()
            .Where(m => m.UserId == userId && m.Team!.Activity!.ActivityDate >= first && m.Team!.Activity!.ActivityDate <= last
                && m.Team!.Activity!.ConfirmedDrawNumber != null && m.Team!.DrawNumber <= m.Team!.Activity!.ConfirmedDrawNumber)
            .Select(m => new { ActivityId = m.Team!.ActivityId, Draw = m.Team!.DrawNumber, m.Team.Name })
            .ToListAsync();

        var records = activities
            .OrderByDescending(a => a.ActivityDate)
            .Select(a =>
            {
                var attendance = myAttendances.FirstOrDefault(x => x.ActivityId == a.Id);
                var teams = myTeams
                    .Where(t => t.ActivityId == a.Id)
                    .OrderBy(t => t.Draw)
                    .Select(t => new TeamAssignmentDto(t.Draw, t.Name))
                    .ToList();
                return new ActivityRecordDto(
                    a.ActivityDate,
                    a.Status,
                    attendance is not null,
                    attendance?.CheckedAt,
                    teams);
            })
            .ToList();

        return new MyRecordsDto(year, month, records, myAttendances.Count, activities.Count);
    }

    public async Task<MySemesterStatsDto> GetMySemesterStatsAsync(string userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var scope = await GetCurrentSemesterScopeAsync(db);
        var (semStart, semEnd) = (scope.Start, scope.End);

        // 실제 개최된 활동(출석이 한 번이라도 열림)만 분모
        var semesterActivities = await db.Activities.AsNoTracking()
            .Where(a => a.ActivityDate >= semStart && a.ActivityDate <= semEnd && a.Status != ActivityStatus.Ready)
            .Select(a => a.Id)
            .ToListAsync();

        var myAttendances = await db.Attendances.AsNoTracking()
            .CountAsync(x => x.UserId == userId && semesterActivities.Contains(x.ActivityId));

        var totalActiveMembers = await db.Users.CountAsync(u => u.IsActive);

        var counts = await db.Attendances.AsNoTracking()
            .Where(x => semesterActivities.Contains(x.ActivityId))
            .GroupBy(x => x.UserId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToListAsync();

        var myCount = counts.FirstOrDefault(c => c.UserId == userId)?.Count ?? 0;
        var rank = counts.Count(c => c.Count > myCount) + 1;

        var rate = semesterActivities.Count == 0 ? 0 : (int)Math.Round(myAttendances * 100.0 / semesterActivities.Count);
        return new MySemesterStatsDto(semesterActivities.Count, myAttendances, rate, rank, totalActiveMembers, scope);
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
            where u.IsActive
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
