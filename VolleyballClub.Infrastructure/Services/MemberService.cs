using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using VolleyballClub.Application.Abstractions;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Application.Exceptions;
using VolleyballClub.Domain;
using VolleyballClub.Domain.Enums;
using VolleyballClub.Infrastructure.Identity;
using VolleyballClub.Infrastructure.Persistence;

namespace VolleyballClub.Infrastructure.Services;

public class MemberService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    UserManager<ApplicationUser> userManager,
    ICurrentUserService currentUser,
    IAuditLogService auditLog,
    ISheetFormatResolver formatResolver,
    TimeProvider timeProvider) : IMemberService
{
    public async Task<MemberPageDto> GetPageAsync(MemberPageRequest request)
    {
        await currentUser.RequireAdminAsync();
        await using var db = await dbFactory.CreateDbContextAsync();

        var today = KstClock.Today(timeProvider);
        var (semStart, semEnd) = await SemesterPeriodResolver.GetCurrentRangeAsync(db, today);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);

        var currentPeriod = await db.FeePeriods.AsNoTracking()
            .Where(p => p.StartDate <= today && p.EndDate >= today)
            .OrderByDescending(p => p.StartDate)
            .FirstOrDefaultAsync();

        var roleByUser = await GetRoleByUserAsync(db);

        // 시트 연결 판별 — 현재 학기 출석 탭 기준(GetUnmappedUserIdsAsync와 동일 규칙)
        var tabName = formatResolver.ResolveByDate(today)?.TabName;
        var sheetIds = await db.SheetDefinitions.AsNoTracking()
            .Where(s => s.Kind == SheetKind.Attendance)
            .Select(s => s.Id)
            .ToListAsync();
        var mappedSet = tabName is null || sheetIds.Count == 0
            ? new HashSet<string>()
            : (await db.SheetMemberMappings.AsNoTracking()
                .Where(m => m.TabName == tabName && sheetIds.Contains(m.SheetDefinitionId))
                .Select(m => m.UserId)
                .ToListAsync()).ToHashSet();

        var paidSet = currentPeriod is null
            ? new HashSet<string>()
            : (await db.FeePayments.AsNoTracking()
                .Where(p => p.FeePeriodId == currentPeriod.Id && p.IsPaid)
                .Select(p => p.UserId)
                .ToListAsync()).ToHashSet();

        var query = db.Users.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim().ToLower();
            query = query.Where(u => u.Name.ToLower().Contains(term) || u.StudentNumber.ToLower().Contains(term));
        }

        if (request.Statuses is { Count: > 0 } statuses)
        {
            query = query.Where(u => statuses.Contains(u.Status));
        }

        if (request.SheetMapped is { } wantMapped)
        {
            query = query.Where(u => mappedSet.Contains(u.Id) == wantMapped);
        }

        if (request.FeePaid is { } wantPaid)
        {
            query = query.Where(u => paidSet.Contains(u.Id) == wantPaid);
        }

        var total = await query.CountAsync();

        var ordered = (request.Sort, request.Ascending) switch
        {
            (MemberSortField.TotalAttendance, true) => query.OrderBy(u => db.Attendances.Count(a => a.UserId == u.Id)),
            (MemberSortField.TotalAttendance, false) => query.OrderByDescending(u => db.Attendances.Count(a => a.UserId == u.Id)),
            (MemberSortField.SemesterAttendance, true) => query.OrderBy(u => db.Attendances.Count(a =>
                a.UserId == u.Id && a.Activity!.ActivityDate >= semStart && a.Activity!.ActivityDate <= semEnd)),
            (MemberSortField.SemesterAttendance, false) => query.OrderByDescending(u => db.Attendances.Count(a =>
                a.UserId == u.Id && a.Activity!.ActivityDate >= semStart && a.Activity!.ActivityDate <= semEnd)),
            (MemberSortField.MonthAttendance, true) => query.OrderBy(u => db.Attendances.Count(a =>
                a.UserId == u.Id && a.Activity!.ActivityDate >= monthStart && a.Activity!.ActivityDate <= monthEnd)),
            _ => query.OrderByDescending(u => db.Attendances.Count(a =>
                a.UserId == u.Id && a.Activity!.ActivityDate >= monthStart && a.Activity!.ActivityDate <= monthEnd)),
        };

        // 동률 행의 2차 정렬 — 페이지를 넘길 때 순서가 흔들려 행이 중복/누락되는 것을 막는다
        var stable = ordered.ThenBy(u => u.Name);

        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 100);

        var raw = await stable
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(u => new
            {
                u.Id,
                u.Name,
                u.StudentNumber,
                u.Department,
                u.Status,
                u.Color,
                Total = db.Attendances.Count(a => a.UserId == u.Id),
                Semester = db.Attendances.Count(a =>
                    a.UserId == u.Id && a.Activity!.ActivityDate >= semStart && a.Activity!.ActivityDate <= semEnd),
                Month = db.Attendances.Count(a =>
                    a.UserId == u.Id && a.Activity!.ActivityDate >= monthStart && a.Activity!.ActivityDate <= monthEnd),
            })
            .ToListAsync();

        var items = raw.Select(u => new MemberListItemDto(
                u.Id,
                u.Name,
                u.StudentNumber,
                u.Department,
                roleByUser.GetValueOrDefault(u.Id, "MEMBER"),
                u.Status,
                u.Color,
                u.Semester,
                u.Month,
                u.Total,
                paidSet.Contains(u.Id),
                mappedSet.Contains(u.Id)))
            .ToList();

        return new MemberPageDto(items, total, page, pageSize);
    }

    public async Task<MemberListDto> GetAsync(string userId)
    {
        await currentUser.RequireAdminAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId)
            ?? throw new NotFoundException("부원을 찾을 수 없습니다.");

        // 단일 사용자 역할만 확인 — 전체 역할 조인은 목록(GetPageAsync)이 담당한다
        var adminRole = await db.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.Name == "ADMIN");
        var isAdmin = adminRole is not null
            && await db.UserRoles.AsNoTracking().AnyAsync(ur => ur.UserId == userId && ur.RoleId == adminRole.Id);

        // 총 출석·회비는 상세 화면에서 GetAdminRecordsAsync가 집계하므로 조회하지 않는다(AddAsync/UpdateAsync와 동일 규약)
        return new MemberListDto(user.Id, user.Name, user.StudentNumber, user.Department,
            isAdmin ? "ADMIN" : "MEMBER", user.Status, 0, false, user.Color);
    }

    /// <summary>감사 로그용 상태 라벨.</summary>
    private static string StatusLabel(MemberStatus status) => status switch
    {
        MemberStatus.Withdrawn => "탈퇴",
        MemberStatus.Expelled => "퇴출",
        _ => "활동",
    };

    /// <summary>UserId → "ADMIN"/"MEMBER" 역할 맵.</summary>
    private async Task<Dictionary<string, string>> GetRoleByUserAsync(ApplicationDbContext db)
    {
        var roles = await (
            from ur in db.UserRoles.AsNoTracking()
            join r in db.Roles.AsNoTracking() on ur.RoleId equals r.Id
            select new { ur.UserId, r.Name }
        ).ToListAsync();

        return roles
            .GroupBy(r => r.UserId)
            .ToDictionary(g => g.Key, g => g.Any(r => r.Name == "ADMIN") ? "ADMIN" : "MEMBER");
    }

    public async Task<MemberListDto> AddAsync(MemberCreateRequest request)
    {
        await currentUser.RequireAdminAsync();

        var studentNumber = request.StudentNumber.Trim();
        if (string.IsNullOrEmpty(studentNumber) || string.IsNullOrEmpty(request.Name.Trim()))
        {
            throw new RuleViolationException("학번과 이름은 필수입니다.");
        }

        if (request.Password.Length < 4)
        {
            throw new RuleViolationException("비밀번호는 4자 이상이어야 합니다.");
        }

        if (await userManager.FindByNameAsync(studentNumber) is not null)
        {
            throw new ConflictException("이미 등록된 학번입니다.");
        }

        var now = KstClock.NowUtc(timeProvider);

        // 현재 색 사용량 기준으로 가장 적게 쓰인 색 중 랜덤 배정(골고루 분산)
        var colorUsage = await userManager.Users
            .Where(u => u.Color != null)
            .GroupBy(u => u.Color!)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);

        var user = new ApplicationUser
        {
            UserName = studentNumber,
            StudentNumber = studentNumber,
            Name = request.Name.Trim(),
            Department = request.Department.Trim(),
            Status = MemberStatus.Active,
            Color = MemberColors.PickBalanced(colorUsage),
            JoinedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var result = await userManager.CreateAsync(user, request.Password);
        if (!result.Succeeded)
        {
            throw new RuleViolationException(string.Join(" ", result.Errors.Select(e => e.Description)));
        }

        var role = request.Role == "ADMIN" ? "ADMIN" : "MEMBER";
        await userManager.AddToRoleAsync(user, role);
        await auditLog.LogAsync(AdminAction.MemberCreate, "User", user.Id, $"{user.Name}({studentNumber}) {role} 추가");

        return new MemberListDto(user.Id, user.Name, user.StudentNumber, user.Department, role, MemberStatus.Active, 0, false, user.Color);
    }

    public async Task<MemberListDto> UpdateAsync(string userId, MemberUpdateRequest request)
    {
        await currentUser.RequireAdminAsync();
        var user = await userManager.FindByIdAsync(userId)
            ?? throw new NotFoundException("부원을 찾을 수 없습니다.");

        if (string.IsNullOrEmpty(request.Name.Trim()))
        {
            throw new RuleViolationException("이름은 필수입니다.");
        }

        // 학번 변경 = 로그인 아이디 변경. 중복 검사 후 반영한다.
        var studentNumber = request.StudentNumber?.Trim();
        if (!string.IsNullOrEmpty(studentNumber) && studentNumber != user.StudentNumber)
        {
            if (await userManager.FindByNameAsync(studentNumber) is not null)
            {
                throw new ConflictException("이미 등록된 학번입니다.");
            }

            user.UserName = studentNumber;
            user.StudentNumber = studentNumber;
        }

        user.Name = request.Name.Trim();
        user.Department = request.Department.Trim();
        user.UpdatedAt = KstClock.NowUtc(timeProvider);

        if (request.Color is { } requestedColor)
        {
            if (!MemberColors.Contains(requestedColor))
            {
                throw new RuleViolationException("지원하지 않는 색상입니다.");
            }

            user.Color = requestedColor;
        }

        if (request.Status is { } status && status != user.Status)
        {
            user.Status = status;
            // 보안 스탬프를 갱신해 기존 로그인 세션/재검증 흐름과 일치시킨다
            await userManager.UpdateSecurityStampAsync(user);
            await auditLog.LogAsync(status == MemberStatus.Active ? AdminAction.MemberEnable : AdminAction.MemberDisable,
                "User", user.Id,
                $"{user.Name} 상태 변경 → {StatusLabel(status)}");
        }

        var updateResult = await userManager.UpdateAsync(user);
        if (!updateResult.Succeeded)
        {
            throw new RuleViolationException(string.Join(" ", updateResult.Errors.Select(e => e.Description)));
        }

        if (request.Role is { } newRole && (newRole == "ADMIN" || newRole == "MEMBER"))
        {
            var currentRoles = await userManager.GetRolesAsync(user);
            if (!currentRoles.Contains(newRole))
            {
                await userManager.RemoveFromRolesAsync(user, currentRoles);
                await userManager.AddToRoleAsync(user, newRole);
            }
        }

        if (!string.IsNullOrEmpty(request.NewPassword))
        {
            if (request.NewPassword.Length < 4)
            {
                throw new RuleViolationException("비밀번호는 4자 이상이어야 합니다.");
            }

            // 관리자 재설정 — 기존 비밀번호 확인 없이 교체한다.
            // Identity 정책으로 사전 검증 후 제거→등록: 등록 실패로 비밀번호 없는 사용자가 남는 것을 방지한다.
            foreach (var validator in userManager.PasswordValidators)
            {
                var vr = await validator.ValidateAsync(userManager, user, request.NewPassword);
                if (!vr.Succeeded)
                {
                    throw new RuleViolationException(string.Join(" ", vr.Errors.Select(e => e.Description)));
                }
            }

            var removeResult = await userManager.RemovePasswordAsync(user);
            if (!removeResult.Succeeded)
            {
                throw new RuleViolationException(string.Join(" ", removeResult.Errors.Select(e => e.Description)));
            }

            var resetResult = await userManager.AddPasswordAsync(user, request.NewPassword);
            if (!resetResult.Succeeded)
            {
                throw new RuleViolationException(string.Join(" ", resetResult.Errors.Select(e => e.Description)));
            }

            await auditLog.LogAsync(AdminAction.MemberPasswordReset, "User", user.Id, $"{user.Name} 비밀번호 초기화");
        }

        await auditLog.LogAsync(AdminAction.MemberUpdate, "User", user.Id, $"{user.Name} 정보 수정");
        return new MemberListDto(user.Id, user.Name, user.StudentNumber, user.Department, request.Role ?? "MEMBER", user.Status, 0, false, user.Color);
    }

    public async Task<ProfileDto> GetProfileAsync(string userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId)
            ?? throw new NotFoundException("부원을 찾을 수 없습니다.");

        var adminRole = await db.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.Name == "ADMIN");
        var isAdmin = adminRole is not null
            && await db.UserRoles.AsNoTracking().AnyAsync(ur => ur.UserId == userId && ur.RoleId == adminRole.Id);
        var totalAttendance = await db.Attendances.AsNoTracking()
            .CountAsync(a => a.UserId == userId);

        return new ProfileDto(user.Name, user.StudentNumber, user.Department, isAdmin ? "ADMIN" : "MEMBER", user.JoinedAt, totalAttendance, user.Color);
    }

    public async Task UpdateMyColorAsync(string color)
    {
        if (!MemberColors.Contains(color))
        {
            throw new RuleViolationException("지원하지 않는 색상입니다.");
        }

        var userId = await currentUser.RequireUserIdAsync();
        var user = await userManager.FindByIdAsync(userId)
            ?? throw new NotFoundException("부원을 찾을 수 없습니다.");
        user.Color = color;
        user.UpdatedAt = KstClock.NowUtc(timeProvider);
        var result = await userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            throw new RuleViolationException(string.Join(" ", result.Errors.Select(e => e.Description)));
        }
    }

    public async Task ChangeMyPasswordAsync(PasswordChangeRequest request)
    {
        if (request.NewPassword.Length < 4)
        {
            throw new RuleViolationException("새 비밀번호는 4자 이상이어야 합니다.");
        }

        var userId = await currentUser.RequireUserIdAsync();
        var user = await userManager.FindByIdAsync(userId)
            ?? throw new NotFoundException("부원을 찾을 수 없습니다.");
        var result = await userManager.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded)
        {
            throw new RuleViolationException(string.Join(" ", result.Errors.Select(e => e.Description)));
        }
    }
}
