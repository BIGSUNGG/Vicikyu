using Microsoft.EntityFrameworkCore;
using VolleyballClub.Application.Abstractions;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Application.Exceptions;
using VolleyballClub.Domain;
using VolleyballClub.Domain.Entities;
using VolleyballClub.Domain.Enums;
using VolleyballClub.Infrastructure.Persistence;

namespace VolleyballClub.Infrastructure.Services;

/// <summary>관리자 활동 관리(DB 단일 원천) — DayMark(예정/없음)와 Activity(완료/진행)를 한 목록으로 다룬다.
/// Key 규약: DayMark는 "mark:{yyyy-MM-dd}", Activity는 "app:{id}".</summary>
public sealed class AdminActivityService(
    IDbContextFactory<ApplicationDbContext> factory,
    ICurrentUserService currentUser,
    TimeProvider clock) : IAdminActivityService
{
    public async Task<IReadOnlyList<ManagedActivityDto>> GetActivitiesAsync(CancellationToken ct = default)
    {
        await currentUser.RequireAdminAsync();
        await using var db = await factory.CreateDbContextAsync(ct);
        return await ListAsync(db, ct);
    }

    private static bool Editable(Activity? activity) => activity is not null && activity.Status != ActivityStatus.Ready;

    private static string State(DayMark mark) => mark.Kind == DayKind.NoActivity
        ? "활동 없음" + (string.IsNullOrWhiteSpace(mark.Note) ? "" : $"({mark.Note})")
        : "활동 예정";

    private static string State(Activity activity) => activity.Status == ActivityStatus.ActivityClosed
        ? "활동 완료" + (string.IsNullOrWhiteSpace(activity.DisplayName) ? "" : $"({activity.DisplayName})")
        : ActivityService.StatusText(activity.Status);

    private static ManagedActivityDto Dto(DayMark m) => new(
        $"mark:{m.Date:yyyy-MM-dd}", null, m.Date, null, m.Kind, m.Note, null, State(m), false, 0);

    private static ManagedActivityDto Dto(Activity a) => new(
        $"app:{a.Id}", a.Id, a.ActivityDate, a.DisplayName, DayKind.Completed, null, a.Status,
        State(a), Editable(a), a.SemesterAttendances.Count);

    private static async Task<List<ManagedActivityDto>> ListAsync(ApplicationDbContext db, CancellationToken ct)
    {
        var marks = await db.DayMarks.AsNoTracking().ToListAsync(ct);
        var activities = await db.Activities.AsNoTracking().Include(a => a.SemesterAttendances).ToListAsync(ct);
        return marks.Select(Dto)
            .Concat(activities.Select(Dto))
            .OrderByDescending(a => a.Date)
            .ThenBy(a => a.Key)
            .ToList();
    }

    private static (string? Label, string? Note) Clean(DayKind kind, string? label, string? note)
    {
        if (label is not null)
        {
            label = label.Trim();
            if (label.Length == 0) label = null;
            else if (label.Length > 100) throw new RuleViolationException("활동 이름은 100자 이하여야 합니다.");
        }
        if (kind == DayKind.NoActivity && !string.IsNullOrWhiteSpace(note)) note = note.Trim();
        else note = null;
        if (note?.Length > 500) throw new RuleViolationException("사유는 500자 이하여야 합니다.");
        return (label, note);
    }

    private static void ValidateKind(DayKind kind)
    {
        if (!Enum.IsDefined(kind)) throw new RuleViolationException("활동 상태가 올바르지 않습니다.");
    }

    public async Task<ManagedActivityDto> CreateAsync(ActivityCreateRequest request, CancellationToken ct = default)
    {
        await currentUser.RequireAdminAsync();
        ValidateKind(request.Kind);
        var (label, note) = Clean(request.Kind, request.Label, request.Note);
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        ManagedActivityDto result;
        if (request.Kind == DayKind.Completed)
        {
            await EnsureNoCollisionAsync(db, request.Date, label, null, ct);
            var adminId = await currentUser.RequireUserIdAsync();
            var now = KstClock.NowUtc(clock);
            var activity = new Activity
            {
                ActivityDate = request.Date,
                DisplayName = label,
                Status = ActivityStatus.ActivityClosed,
                CreatedByUserId = adminId,
                CreatedAt = now,
                UpdatedAt = now,
                AttendanceOpenedAt = now,
                AttendanceClosedAt = now,
                ActivityClosedAt = now,
            };
            db.Activities.Add(activity);
            // 실제 활동이 예정/없음 표시를 대체한다
            db.DayMarks.RemoveRange(db.DayMarks.Where(m => m.Date == request.Date));
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            await AuditAsync(db, AdminAction.ActivityCreate, request.Date,
                $"활동 완료 생성{(label is null ? "" : $"({label})")} — 출석은 목록에서 편집", ct);
            result = await FindDtoAsync(db, $"app:{activity.Id}", ct);
        }
        else
        {
            var existing = await db.DayMarks.SingleOrDefaultAsync(m => m.Date == request.Date, ct);
            if (existing is not null && existing.Kind == request.Kind && existing.Note == note)
            {
                await transaction.RollbackAsync(ct);
                return await FindDtoAsync(db, $"mark:{request.Date:yyyy-MM-dd}", ct); // 유실된 응답 후 재시도
            }
            if (existing is null && await db.Activities.AnyAsync(a => a.ActivityDate == request.Date, ct))
                throw new ConflictException("그 날짜에 이미 활동이 있습니다.");
            if (existing is not null) db.DayMarks.Remove(existing);
            var mark = new DayMark { Date = request.Date, Kind = request.Kind, Note = note };
            db.DayMarks.Add(mark);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            await AuditAsync(db, AdminAction.ActivityCreate, request.Date,
                request.Kind == DayKind.Scheduled ? "활동 예정 생성" : "활동 없음 표시" + (note is null ? "" : $"({note})"), ct);
            result = await FindDtoAsync(db, $"mark:{request.Date:yyyy-MM-dd}", ct);
        }

        return result;
    }

    /// <summary>같은 날짜·같은 라벨의 완료 활동 충돌 방지(라벨 없음끼리도).</summary>
    private static async Task EnsureNoCollisionAsync(ApplicationDbContext db, DateOnly date, string? label, int? exceptActivityId, CancellationToken ct)
    {
        var collision = await db.Activities.AnyAsync(a => a.ActivityDate == date && a.DisplayName == label && a.Id != exceptActivityId, ct);
        if (collision)
            throw new ConflictException(label is null
                ? "그 날짜에 이미 정기 활동이 있습니다."
                : $"그 날짜에 이미 '{label}' 활동이 있습니다.");
    }

    private async Task AuditAsync(ApplicationDbContext db, AdminAction action, DateOnly date, string verb, CancellationToken ct)
    {
        db.AuditLogs.Add(new AuditLog
        {
            AdminUserId = await currentUser.RequireUserIdAsync(),
            Action = action,
            TargetType = "Activity",
            TargetId = date.ToString("yyyy-MM-dd"),
            Details = $"{date:yyyy-MM-dd} {verb}",
            CreatedAt = KstClock.NowUtc(clock),
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>출석자·팀·게스트가 남아 있는 활동의 예정/없음 전환을 거부한다(데이터 소실 방지).</summary>
    private static void EnsureNoAttendanceToMarkChange(Activity activity)
    {
        if (activity.SemesterAttendances.Count > 0)
            throw new ConflictException("출석자가 있는 활동은 예정/없음으로 바꿀 수 없습니다.");
        if (activity.Teams.Count > 0 || activity.Guests.Count > 0)
            throw new ConflictException("팀·게스트 기록이 있는 활동은 예정/없음으로 바꿀 수 없습니다.");
    }

    public Task<ManagedActivityDto> EditAsync(string key, ActivityEditRequest request, CancellationToken ct = default) =>
        request.Attendances is { Count: > 0 }
            ? SaveWithAttendancesAsync(key, request, ct)
            : EditCoreAsync(key, request, ct);

    /// <summary>변경 저장 1회 = 활동 편집 적용 후 출석 일괄 적용(변경 저장 화면 흐름).</summary>
    private async Task<ManagedActivityDto> SaveWithAttendancesAsync(string key, ActivityEditRequest request, CancellationToken ct)
    {
        // 편집이 먼저 커밋되므로 출석 적용 가능성은 커밋 전에 검증한다
        if (request.Kind is DayKind.Scheduled or DayKind.NoActivity)
            throw new ConflictException("예정/없음으로 변경하는 저장에는 출석 변경을 포함할 수 없습니다. 상태만 먼저 저장하세요.");
        var dto = await EditCoreAsync(key, request, ct);
        if (dto.ActivityId is null)
            throw new ConflictException("예정/없음 표시에는 출석을 변경할 수 없습니다.");
        foreach (var attendance in request.Attendances!)
        {
            await SetAttendanceAsync(dto.Key, attendance.UserId, new(attendance.IsAttended, ConfirmTeamImpact: true), ct);
        }
        await using var db = await factory.CreateDbContextAsync(ct);
        return await FindDtoAsync(db, dto.Key, ct);
    }

    private async Task<ManagedActivityDto> EditCoreAsync(string key, ActivityEditRequest request, CancellationToken ct)
    {
        await currentUser.RequireAdminAsync();
        var kind = request.Kind;
        if (kind is not null) ValidateKind(kind.Value);
        var (label, note) = Clean(kind ?? DayKind.Completed, request.Label, request.Note);
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        if (kind is null && request.Label is not null && key.StartsWith("app:"))
        {
            // 종류 유지·라벨만 변경
            var activity = await LoadActivityAsync(db, key, ct);
            await EnsureNoCollisionAsync(db, request.Date, label, activity.Id, ct);
            activity.DisplayName = label;
            if (activity.ActivityDate != request.Date)
            {
                activity.ActivityDate = request.Date;
                activity.UpdatedAt = KstClock.NowUtc(clock);
            }
            db.DayMarks.RemoveRange(db.DayMarks.Where(m => m.Date == request.Date));
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            await AuditAsync(db, AdminAction.SettingChange, request.Date, $"활동 수정{(label is null ? "" : $"({label})")}", ct);
            return await FindDtoAsync(db, key, ct);
        }

        var effectiveKind = kind ?? (key.StartsWith("mark:") ? (await db.DayMarks.SingleAsync(m => m.Date == MarkDate(key), ct)).Kind : DayKind.Completed);
        // 날짜 이동 대상에 같은 종류 항목이 있으면 거부(마크 PK·활동 라벨 충돌)
        if (request.Date != (key.StartsWith("mark:") ? MarkDate(key) : (await LoadActivityAsync(db, key, ct)).ActivityDate))
        {
            if (effectiveKind != DayKind.Completed && await db.DayMarks.AnyAsync(m => m.Date == request.Date, ct))
                throw new ConflictException("이동하려는 날짜에 이미 예정/없음 표시가 있습니다.");
            if (effectiveKind != DayKind.Completed && await db.Activities.AnyAsync(a => a.ActivityDate == request.Date, ct))
                throw new ConflictException("이동하려는 날짜에 이미 활동이 있습니다.");
        }

        ManagedActivityDto result;
        if (effectiveKind == DayKind.Completed)
        {
            // 완료 활동으로 통일 — 마크였으면 마크를 지우고 활동 생성
            Activity activity;
            if (key.StartsWith("app:"))
            {
                activity = await LoadActivityAsync(db, key, ct);
                if (request.Kind is not null && activity.Status != ActivityStatus.ActivityClosed)
                {
                    // 진행 중·준비 중 → 완료 전환(마감). 준비 중 활동도 관리자가 완료를 지정하면 즉시 마감한다
                    // — 완료 요청을 조용히 무시하면 화면에는 '변경 완료'로 보이고 상태만 그대로 남는 무응답 실패가 된다
                    activity.Status = ActivityStatus.ActivityClosed;
                    var now = KstClock.NowUtc(clock);
                    activity.ActivityClosedAt = now;
                    activity.AttendanceOpenedAt ??= now;
                    activity.AttendanceClosedAt ??= now;
                    activity.UpdatedAt = now;
                    db.Teams.RemoveRange(db.Teams.Where(t => t.ActivityId == activity.Id));
                }
            }
            else
            {
                var adminId = await currentUser.RequireUserIdAsync();
                var now = KstClock.NowUtc(clock);
                activity = new Activity
                {
                    ActivityDate = request.Date,
                    DisplayName = label,
                    Status = ActivityStatus.ActivityClosed,
                    CreatedByUserId = adminId,
                    CreatedAt = now,
                    UpdatedAt = now,
                    AttendanceOpenedAt = now,
                    AttendanceClosedAt = now,
                    ActivityClosedAt = now,
                };
                db.Activities.Add(activity);
                db.DayMarks.Remove(await db.DayMarks.SingleAsync(m => m.Date == MarkDate(key), ct));
            }
            await EnsureNoCollisionAsync(db, request.Date, label, activity.Id, ct);
            activity.ActivityDate = request.Date;
            if (request.Label is not null) activity.DisplayName = label;
            activity.UpdatedAt = KstClock.NowUtc(clock);
            db.DayMarks.RemoveRange(db.DayMarks.Where(m => m.Date == request.Date));
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            await AuditAsync(db, AdminAction.SettingChange, request.Date, $"활동 수정{(label is null ? "" : $"({label})")}", ct);
            result = await FindDtoAsync(db, $"app:{activity.Id}", ct);
        }
        else
        {
            // 예정/없음 마크로 통일 — 활동이었으면 출석 없는 경우에만 마크로 되돌린다
            DayMark mark;
            if (key.StartsWith("mark:"))
            {
                mark = await db.DayMarks.SingleAsync(m => m.Date == MarkDate(key), ct);
                db.DayMarks.Remove(mark);
                await db.SaveChangesAsync(ct);
                mark = new DayMark { Date = request.Date, Kind = effectiveKind, Note = note };
                db.DayMarks.Add(mark);
            }
            else
            {
                var activityId = int.Parse(key[4..]);
                var activity = await db.Activities
                    .Include(a => a.SemesterAttendances).Include(a => a.Teams).Include(a => a.Guests)
                    .SingleAsync(a => a.Id == activityId, ct);
                EnsureNoAttendanceToMarkChange(activity);
                mark = new DayMark { Date = request.Date, Kind = effectiveKind, Note = note };
                db.DayMarks.Add(mark);
                db.Activities.Remove(activity);
            }
            if (await db.DayMarks.AnyAsync(m => m.Date == request.Date && m != mark, ct))
                throw new ConflictException("그 날짜에 이미 예정/없음 표시가 있습니다.");
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            await AuditAsync(db, AdminAction.SettingChange, request.Date,
                effectiveKind == DayKind.Scheduled ? "활동 예정 수정" : "활동 없음 수정" + (note is null ? "" : $"({note})"), ct);
            result = await FindDtoAsync(db, $"mark:{request.Date:yyyy-MM-dd}", ct);
        }

        return result;
    }

    private static DateOnly MarkDate(string key) => DateOnly.ParseExact(key[5..], "yyyy-MM-dd");

    private static async Task<Activity> LoadActivityAsync(ApplicationDbContext db, string key, CancellationToken ct)
    {
        if (!key.StartsWith("app:") || !int.TryParse(key[4..], out var id))
            throw new NotFoundException("활동을 찾을 수 없습니다.");
        return await db.Activities.Include(a => a.SemesterAttendances).SingleOrDefaultAsync(a => a.Id == id, ct)
            ?? throw new NotFoundException("활동을 찾을 수 없습니다.");
    }

    private static async Task<ManagedActivityDto> FindDtoAsync(ApplicationDbContext db, string key, CancellationToken ct)
    {
        if (key.StartsWith("mark:"))
        {
            var mark = await db.DayMarks.AsNoTracking().SingleOrDefaultAsync(m => m.Date == MarkDate(key), ct);
            if (mark is not null) return Dto(mark);
        }
        else
        {
            var activityId = int.Parse(key[4..]);
            var activity = await db.Activities.AsNoTracking().Include(a => a.SemesterAttendances)
                .SingleOrDefaultAsync(a => a.Id == activityId, ct);
            if (activity is not null) return Dto(activity);
        }
        // 편집으로 key 자체가 바뀐 경우(마크↔활동 전환) — 목록에서 날짜로 다시 찾는다
        var date = key.StartsWith("mark:") ? MarkDate(key) : DateOnly.MinValue;
        var all = await ListAsync(db, ct);
        return all.SingleOrDefault(a => a.Date == date || a.Key == key)
            ?? throw new NotFoundException("활동을 찾을 수 없습니다.");
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        await currentUser.RequireAdminAsync();
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (key.StartsWith("mark:"))
        {
            var mark = await db.DayMarks.SingleOrDefaultAsync(m => m.Date == MarkDate(key), ct);
            if (mark is null) return; // 이미 커밋된 삭제의 재시도
            db.DayMarks.Remove(mark);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            await AuditAsync(db, AdminAction.ActivityCancel, mark.Date, "예정/없음 표시 삭제", ct);
            return;
        }

        var activity = await LoadActivityAsync(db, key, ct);
        var (attendanceCount, teamCount, guestCount) =
            (activity.SemesterAttendances.Count, await db.Teams.CountAsync(t => t.ActivityId == activity.Id, ct),
             await db.Guests.CountAsync(g => g.ActivityId == activity.Id, ct));
        db.SemesterAttendances.RemoveRange(activity.SemesterAttendances);
        db.Activities.Remove(activity);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await AuditAsync(db, AdminAction.ActivityCancel, activity.ActivityDate,
            $"활동 삭제 (출석 {attendanceCount}·게스트 {guestCount}·팀 {teamCount} 삭제)", ct);
    }

    public async Task<IReadOnlyList<AttendanceMemberDto>> GetMembersAsync(string key, CancellationToken ct = default)
    {
        await currentUser.RequireAdminAsync();
        if (!key.StartsWith("app:")) return []; // 예정/없음 표시에는 출석이 없다
        await using var db = await factory.CreateDbContextAsync(ct);
        if (!int.TryParse(key[4..], out var activityId)) throw new NotFoundException("활동을 찾을 수 없습니다.");
        var attendance = await db.SemesterAttendances.AsNoTracking()
            .Where(a => a.ActivityId == activityId && a.SemesterMember.MappedUserId != null)
            .Select(a => new { a.Id, a.CheckedAt, a.AttendanceType, UserId = a.SemesterMember.MappedUserId! })
            .ToDictionaryAsync(a => a.UserId, ct);
        // 활동 종료 여부와 무관하게 전체 활성 부원을 나열한다(과거 활동도 임의 출석 부여 가능)
        var users = await db.Users.AsNoTracking().Where(u => u.Status == MemberStatus.Active).OrderBy(u => u.Name).ToListAsync(ct);
        return users.Select(u => { var a = attendance.GetValueOrDefault(u.Id); return new AttendanceMemberDto(u.Id, u.Name, u.StudentNumber,
            u.Department, a is not null, a?.CheckedAt, a?.AttendanceType, a?.Id ?? 0); }).ToList();
    }

    public async Task SetAttendanceAsync(string key, string userId, ActivityAttendanceRequest request, CancellationToken ct = default)
    {
        await currentUser.RequireAdminAsync();
        await using var db = await factory.CreateDbContextAsync(ct);
        var activity = await LoadActivityAsync(db, key, ct);
        if (!Editable(activity))
            throw new ConflictException("시작 또는 완료된 활동만 출석을 수정할 수 있습니다.");
        var member = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct) ?? throw new NotFoundException("부원을 찾을 수 없습니다.");
        var existing = await db.SemesterAttendances
            .FirstOrDefaultAsync(x => x.ActivityId == activity.Id && x.SemesterMember.MappedUserId == userId, ct);
        var teams = await db.TeamMembers.Where(m => m.UserId == userId && m.Team!.ActivityId == activity.Id).ToListAsync(ct);
        if (!request.IsAttended && teams.Count > 0 && !request.ConfirmTeamImpact)
            throw new ConflictException("팀 편성이 완료되어 있습니다. 출석을 취소하면 팀에서도 제외됩니다. 계속하시겠습니까?");

        var attendanceChanged = (existing is not null) != request.IsAttended;
        var now = KstClock.NowUtc(clock);
        if (attendanceChanged && request.IsAttended)
        {
            var row = await SemesterMemberRows.EnsureUserRowAsync(db, userId, activity, now, ct);
            db.SemesterAttendances.Add(new SemesterAttendance
            {
                SemesterMember = row,
                ActivityId = activity.Id,
                AttendanceType = AttendanceType.Admin,
                CheckedAt = now,
                CreatedAt = now,
            });
        }
        else if (attendanceChanged) { db.SemesterAttendances.Remove(existing!); db.TeamMembers.RemoveRange(teams); }
        if (attendanceChanged)
        {
            activity.UpdatedAt = now;
            db.AuditLogs.Add(new AuditLog
            {
                AdminUserId = await currentUser.RequireUserIdAsync(),
                Action = request.IsAttended ? AdminAction.AttendanceAdd : AdminAction.AttendanceRemove,
                TargetType = "User", TargetId = userId,
                Details = $"{member.Name} {activity.ActivityDate:yyyy-MM-dd} 출석 {(request.IsAttended ? "추가" : "취소")}",
                CreatedAt = now,
            });
        }
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // OTP 체크인과의 경합 등 unique 위반 — AttendanceService와 동일한 방어
            throw new ConflictException("이미 출석 처리되었습니다.");
        }
    }

    public async Task<MemberActivityListDto> GetMemberActivitiesAsync(string userId, CancellationToken ct = default)
    {
        await currentUser.RequireAdminAsync();
        await using var db = await factory.CreateDbContextAsync(ct);
        if (!await db.Users.AnyAsync(u => u.Id == userId, ct)) throw new NotFoundException("부원을 찾을 수 없습니다.");
        var rows = await ListAsync(db, ct);
        var attendance = await db.SemesterAttendances.AsNoTracking()
            .Where(a => a.SemesterMember.MappedUserId == userId).Select(a => new { a.ActivityId, a.Activity!.ActivityDate }).ToListAsync(ct);
        var teams = await db.TeamMembers.AsNoTracking().Where(m => m.UserId == userId).Select(m => m.Team!.ActivityId).ToListAsync(ct);
        var today = KstClock.Today(clock);
        var (from, to) = await SemesterPeriodResolver.GetCurrentRangeAsync(db, today);
        // 이 부원에게 귀속되는 학기의 활동과 실제 출석한 활동만 나타낸다
        var attributedPeriods = await MemberSemesters.GetAttributedPeriodsAsync(db, userId, ct);
        var attendedActivityIds = attendance.Select(x => x.ActivityId).ToHashSet();
        return new(userId, attendance.Count(a => a.ActivityDate.Year == today.Year && a.ActivityDate.Month == today.Month),
            attendance.Count(a => a.ActivityDate >= from && a.ActivityDate <= to), attendance.Count,
            rows.Where(a => a.ActivityId is not null
                    && (MemberSemesters.IsAttributed(a.Date, attributedPeriods) || attendedActivityIds.Contains(a.ActivityId.Value)))
                .Select(a => new MemberActivityRowDto(a.Key, a.ActivityId, a.Date, a.Label, a.StateText,
                    attendance.Any(x => x.ActivityId == a.ActivityId), a.CanEditAttendance,
                    a.ActivityId is { } tid && teams.Contains(tid))).ToList());
    }
}
