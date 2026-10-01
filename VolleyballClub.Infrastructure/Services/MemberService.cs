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
    TimeProvider timeProvider) : IMemberService
{
    public async Task<List<MemberListDto>> GetListAsync(string? search = null)
    {
        await currentUser.RequireAdminAsync();
        await using var db = await dbFactory.CreateDbContextAsync();

        var today = KstClock.Today(timeProvider);
        var currentPeriod = await db.FeePeriods.AsNoTracking()
            .Where(p => p.StartDate <= today && p.EndDate >= today)
            .OrderByDescending(p => p.StartDate)
            .FirstOrDefaultAsync();

        var roles = await (
            from ur in db.UserRoles.AsNoTracking()
            join r in db.Roles.AsNoTracking() on ur.RoleId equals r.Id
            select new { ur.UserId, r.Name }
        ).ToListAsync();
        var roleByUser = roles
            .GroupBy(r => r.UserId)
            .ToDictionary(g => g.Key, g => g.Any(r => r.Name == "ADMIN") ? "ADMIN" : "MEMBER");

        var totalAttendance = await db.Attendances.AsNoTracking()
            .GroupBy(a => a.UserId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToListAsync();
        var attendanceByUser = totalAttendance.ToDictionary(x => x.UserId, x => x.Count);

        var paidUsers = currentPeriod is null
            ? []
            : await db.FeePayments.AsNoTracking()
                .Where(p => p.FeePeriodId == currentPeriod.Id && p.IsPaid)
                .Select(p => p.UserId)
                .ToListAsync();
        var paidSet = paidUsers.ToHashSet();

        var query = db.Users.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(u => u.Name.ToLower().Contains(term) || u.StudentNumber.ToLower().Contains(term));
        }

        var users = await query.OrderBy(u => u.Name).ToListAsync();

        return users.Select(u => new MemberListDto(
                u.Id,
                u.Name,
                u.StudentNumber,
                u.Department,
                roleByUser.GetValueOrDefault(u.Id, "MEMBER"),
                u.IsActive,
                attendanceByUser.GetValueOrDefault(u.Id, 0),
                paidSet.Contains(u.Id)))
            .ToList();
    }

    public async Task<MemberListDto> AddAsync(MemberCreateRequest request)
    {
        await currentUser.RequireAdminAsync();

        var studentNumber = request.StudentNumber.Trim();
        if (string.IsNullOrEmpty(studentNumber) || string.IsNullOrEmpty(request.Name.Trim()))
        {
            throw new RuleViolationException("학번과 이름은 필수입니다.");
        }

        if (request.Password.Length < 8)
        {
            throw new RuleViolationException("비밀번호는 8자 이상이어야 합니다.");
        }

        if (await userManager.FindByNameAsync(studentNumber) is not null)
        {
            throw new ConflictException("이미 등록된 학번입니다.");
        }

        var now = KstClock.NowUtc(timeProvider);
        var user = new ApplicationUser
        {
            UserName = studentNumber,
            StudentNumber = studentNumber,
            Name = request.Name.Trim(),
            Department = request.Department.Trim(),
            IsActive = true,
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

        return new MemberListDto(user.Id, user.Name, user.StudentNumber, user.Department, role, true, 0, false);
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

        if (request.IsActive is { } active && active != user.IsActive)
        {
            user.IsActive = active;
            // 보안 스탬프를 갱신해 기존 로그인 세션/재검증 흐름과 일치시킨다
            await userManager.UpdateSecurityStampAsync(user);
            await auditLog.LogAsync(active ? AdminAction.MemberEnable : AdminAction.MemberDisable,
                "User", user.Id, $"{user.Name} {(active ? "활성화" : "비활성화")}");
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

        await auditLog.LogAsync(AdminAction.MemberUpdate, "User", user.Id, $"{user.Name} 정보 수정");
        return new MemberListDto(user.Id, user.Name, user.StudentNumber, user.Department, request.Role ?? "MEMBER", user.IsActive, 0, false);
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

        return new ProfileDto(user.Name, user.StudentNumber, user.Department, isAdmin ? "ADMIN" : "MEMBER", user.JoinedAt, totalAttendance);
    }
}
