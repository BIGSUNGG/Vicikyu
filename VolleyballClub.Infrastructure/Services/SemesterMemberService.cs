using Microsoft.EntityFrameworkCore;
using VolleyballClub.Application.Abstractions;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Application.Exceptions;
using VolleyballClub.Domain;
using VolleyballClub.Domain.Entities;
using VolleyballClub.Domain.Enums;
using VolleyballClub.Infrastructure.Persistence;

namespace VolleyballClub.Infrastructure.Services;

/// <summary>학기별 부원 행 관리(관리자) — 설정>시트 설정·부원 상세 시트 연결·대량 추가 추천의 공개 계약 구현.
/// 행은 영구 보존되며 매핑은 연결만 바꾼다(삭제 아님).</summary>
public sealed class SemesterMemberService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ICurrentUserService currentUser,
    IAuditLogService auditLog,
    TimeProvider timeProvider) : ISemesterMemberService
{
    public async Task<IReadOnlyList<SemesterMemberRowDto>> GetListAsync(int? semesterId = null, bool? mapped = null, CancellationToken ct = default)
    {
        await currentUser.RequireAdminAsync();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // semesterId: null=전체, 0=미분류(학기 없음), 그 외=해당 학기
        var rows = db.SemesterMembers.AsNoTracking()
            .Where(m => semesterId == null
                || (semesterId == 0 ? m.SemesterId == null : m.SemesterId == semesterId));
        if (mapped is { } wantMapped)
        {
            rows = wantMapped
                ? rows.Where(m => m.MappedUserId != null)
                : rows.Where(m => m.MappedUserId == null);
        }
        var list = await rows
            .OrderBy(m => m.Name).ThenBy(m => m.Id)
            .Select(m => new
            {
                m.Id, m.SemesterId, SemesterName = m.Semester!.Name, m.Label, m.Name, m.MappedUserId, m.CreatedAt,
                AttendanceCount = m.Attendances.Count,
            })
            .ToListAsync(ct);
        var mappedIds = list.Where(r => r.MappedUserId != null).Select(r => r.MappedUserId!).Distinct().ToList();
        var namesById = mappedIds.Count == 0
            ? []
            : await db.Users.AsNoTracking().Where(u => mappedIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Name, ct);
        return list.Select(r => new SemesterMemberRowDto(
                r.Id, r.SemesterId, r.SemesterName, r.Label, r.Name, r.MappedUserId,
                r.MappedUserId is null ? null : namesById.GetValueOrDefault(r.MappedUserId), r.AttendanceCount, r.CreatedAt))
            .ToList();
    }

    public async Task SetMappingAsync(int rowId, string? userId, CancellationToken ct = default)
    {
        await currentUser.RequireAdminAsync();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var row = await db.SemesterMembers.Include(m => m.Attendances)
            .SingleOrDefaultAsync(m => m.Id == rowId, ct)
            ?? throw new NotFoundException("학기별 부원 행을 찾을 수 없습니다.");

        if (userId is null)
        {
            row.MappedUserId = null;
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            await auditLog.LogAsync(AdminAction.MemberUpdate, "SemesterMember", rowId.ToString(),
                $"학기별 부원 행 '{row.Label}' 매핑 해제(행·출석 유지)");
            return;
        }

        var member = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new NotFoundException("부원을 찾을 수 없습니다.");
        // 같은 학기에 한 계정은 한 행만 — 실제 학기에서만 적용(미분류 null 행은 DB 인덱스와 동일하게 여러 행 허용)
        var conflict = row.SemesterId != null && await db.SemesterMembers.AnyAsync(
            m => m.Id != rowId && m.MappedUserId == userId && m.SemesterId == row.SemesterId, ct);
        if (conflict)
            throw new ConflictException("이 부원에게 이미 이 학기에 연결된 행이 있습니다. 먼저 매핑을 해제해 주세요.");

        row.MappedUserId = userId;

        // 같은 활동의 출석이 이 부원의 다른 행에 이미 있으면 중복 제거(행 병합 시 이쪽 행의 것을 버린다)
        var duplicates = await db.SemesterAttendances
            .Where(sa => sa.SemesterMemberId == rowId
                && db.SemesterAttendances.Any(other =>
                    other.SemesterMemberId != sa.SemesterMemberId
                    && other.SemesterMember.MappedUserId == userId
                    && other.ActivityId == sa.ActivityId))
            .ToListAsync(ct);
        db.SemesterAttendances.RemoveRange(duplicates);

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await auditLog.LogAsync(AdminAction.MemberUpdate, "SemesterMember", rowId.ToString(),
            $"학기별 부원 행 '{row.Label}' → {member.Name} 매핑(영구 보존{(duplicates.Count > 0 ? $", 중복 출석 {duplicates.Count}건 정리" : "")})");
    }

    public async Task<SemesterMemberRowDto> AddRowAsync(SemesterMemberCreateRequest request, CancellationToken ct = default)
    {
        await currentUser.RequireAdminAsync();
        var name = request.Name.Trim();
        if (name.Length == 0) throw new RuleViolationException("이름을 입력해 주세요.");
        var label = string.IsNullOrWhiteSpace(request.Label) ? name : request.Label.Trim();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var semester = request.SemesterId == 0
            ? null
            : await db.SemesterPeriods.AsNoTracking().SingleOrDefaultAsync(p => p.Id == request.SemesterId, ct)
                ?? throw new NotFoundException("학기를 찾을 수 없습니다.");
        var semesterId = semester?.Id; // null = 미분류 — EF가 == null을 IS NULL로 번역한다
        var exists = await db.SemesterMembers.AnyAsync(
            m => m.SemesterId == semesterId && m.Label == label, ct);
        if (exists) throw new ConflictException("같은 학기에 같은 라벨의 행이 이미 있습니다.");

        var row = new SemesterMember
        {
            SemesterId = semesterId,
            Label = label,
            Name = name,
            CreatedAt = KstClock.NowUtc(timeProvider),
        };
        db.SemesterMembers.Add(row);
        await db.SaveChangesAsync(ct);
        await auditLog.LogAsync(AdminAction.MemberUpdate, "SemesterMember", row.Id.ToString(),
            $"학기별 부원 행 '{label}' 추가{(semester is null ? "(미분류)" : $"({semester.Name})")}");
        return new SemesterMemberRowDto(row.Id, row.SemesterId, semester?.Name, row.Label, row.Name, null, null, 0, row.CreatedAt);
    }

    public async Task RemoveRowAsync(int rowId, CancellationToken ct = default)
    {
        await currentUser.RequireAdminAsync();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.SemesterMembers.Include(m => m.Attendances)
            .SingleOrDefaultAsync(m => m.Id == rowId, ct)
            ?? throw new NotFoundException("학기별 부원 행을 찾을 수 없습니다.");
        db.SemesterAttendances.RemoveRange(row.Attendances);
        db.SemesterMembers.Remove(row);
        await db.SaveChangesAsync(ct);
        await auditLog.LogAsync(AdminAction.MemberUpdate, "SemesterMember", rowId.ToString(),
            $"학기별 부원 행 '{row.Label}' 삭제(출석 {row.Attendances.Count}건 포함)");
    }

    public async Task<SemesterRowAttendanceDto> GetRowAttendanceAsync(int rowId, CancellationToken ct = default)
    {
        await currentUser.RequireAdminAsync();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.SemesterMembers.AsNoTracking().Include(m => m.Semester)
            .SingleOrDefaultAsync(m => m.Id == rowId, ct)
            ?? throw new NotFoundException("학기별 부원 행을 찾을 수 없습니다.");
        var attended = await db.SemesterAttendances.AsNoTracking()
            .Where(sa => sa.SemesterMemberId == rowId).Select(sa => sa.ActivityId).ToListAsync(ct);

        // 편집 대상 활동 — 행의 학기(없으면 전체)의 완료·마감 활동
        var activities = await db.Activities.AsNoTracking()
            .Where(a => row.Semester == null
                || (a.ActivityDate >= row.Semester.StartDate && a.ActivityDate <= row.Semester.EndDate))
            .OrderByDescending(a => a.ActivityDate)
            .Select(a => new { a.Id, a.ActivityDate, a.DisplayName })
            .ToListAsync(ct);
        return new SemesterRowAttendanceDto(
            rowId,
            row.Label,
            row.Semester?.Name,
            activities.Select(a => new SemesterRowActivityDto(a.Id, a.ActivityDate, a.DisplayName, attended.Contains(a.Id))).ToList());
    }

    public async Task SetRowAttendanceAsync(int rowId, int activityId, bool attended, CancellationToken ct = default)
    {
        await currentUser.RequireAdminAsync();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.SemesterMembers.SingleOrDefaultAsync(m => m.Id == rowId, ct)
            ?? throw new NotFoundException("학기별 부원 행을 찾을 수 없습니다.");
        var activity = await db.Activities.AsNoTracking().SingleOrDefaultAsync(a => a.Id == activityId, ct)
            ?? throw new NotFoundException("활동을 찾을 수 없습니다.");
        var existing = await db.SemesterAttendances
            .SingleOrDefaultAsync(sa => sa.SemesterMemberId == rowId && sa.ActivityId == activityId, ct);

        // 이중 집계 방지 — 이 행이 매핑된 계정의 다른 행에 같은 활동 출석이 이미 있으면 추가를 거부한다
        if (attended && existing is null && row.MappedUserId is not null)
        {
            var dupOnOtherRow = await db.SemesterAttendances.AnyAsync(sa =>
                sa.ActivityId == activityId
                && sa.SemesterMemberId != rowId
                && sa.SemesterMember.MappedUserId == row.MappedUserId, ct);
            if (dupOnOtherRow)
                throw new ConflictException("이 부원의 다른 학기 행에 이미 이 활동 출석이 있습니다. 먼저 그 쪽을 취소해 주세요.");
        }
        var now = KstClock.NowUtc(timeProvider);

        if (attended && existing is null)
        {
            db.SemesterAttendances.Add(new SemesterAttendance
            {
                SemesterMemberId = rowId,
                ActivityId = activityId,
                AttendanceType = AttendanceType.Admin,
                CheckedAt = now,
                CreatedAt = now,
            });
        }
        else if (!attended && existing is not null)
        {
            db.SemesterAttendances.Remove(existing);
        }
        await db.SaveChangesAsync(ct);
        await auditLog.LogAsync(
            attended ? AdminAction.AttendanceAdd : AdminAction.AttendanceRemove,
            "SemesterMember", rowId.ToString(),
            $"'{row.Label}' {activity.ActivityDate:yyyy-MM-dd} 출석 {(attended ? "추가" : "취소")}");
    }

    public async Task<MemberSheetRowsDto> GetForMemberAsync(string userId, CancellationToken ct = default)
    {
        await currentUser.RequireAdminAsync();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var member = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new NotFoundException("부원을 찾을 수 없습니다.");
        var periods = await db.SemesterPeriods.AsNoTracking().OrderByDescending(p => p.StartDate).ToListAsync(ct);
        var rows = await db.SemesterMembers.AsNoTracking().Include(m => m.Attendances).ToListAsync(ct);

        // 학기별: 이 부원의 매핑 행(있다면) + 후보(미매핑 행). 미분류(null 학기)도 마지막 항목으로 표시한다.
        var groups = new List<MemberSemesterSheetDto>();
        foreach (var period in periods)
        {
            groups.Add(BuildSemesterGroup(period.Id, period.Name, userId, rows));
        }
        if (rows.Any(m => m.SemesterId == null))
        {
            groups.Add(BuildSemesterGroup(null, "미분류", userId, rows));
        }

        return new MemberSheetRowsDto(userId, member.Name, groups);
    }

    private static MemberSemesterSheetDto BuildSemesterGroup(int? semesterId, string semesterName, string userId, List<SemesterMember> rows)
    {
        var inSemester = rows.Where(m => m.SemesterId == semesterId).ToList();
        var mapped = inSemester.FirstOrDefault(m => m.MappedUserId == userId);
        var candidates = inSemester
            .Where(m => m.MappedUserId == null)
            .OrderBy(m => m.Name).ThenBy(m => m.Id)
            .Select(m => new SemesterMemberRowDto(
                m.Id, m.SemesterId, semesterName, m.Label, m.Name, null, null, m.Attendances.Count, m.CreatedAt))
            .ToList();
        return new MemberSemesterSheetDto(
            semesterId,
            semesterName,
            mapped?.Id,
            mapped?.Label,
            mapped?.Attendances.Count ?? 0,
            candidates);
    }
}
