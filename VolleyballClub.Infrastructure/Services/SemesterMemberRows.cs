using Microsoft.EntityFrameworkCore;
using VolleyballClub.Domain.Entities;
using VolleyballClub.Infrastructure.Identity;
using VolleyballClub.Infrastructure.Persistence;

namespace VolleyballClub.Infrastructure.Services;

/// <summary>학기별 부원 행의 공통 규칙 — 날짜→학기 해석, 라이브 출석(OTP·관리자)용 행 보장.
/// 라이브 출석도 학기별 행에 기록되므로 모든 출석 경로가 이 헬퍼를 지난다.</summary>
internal static class SemesterMemberRows
{
    /// <summary>날짜가 속한 학기 ID — 어느 학기에도 없으면 null(미분류).</summary>
    public static async Task<int?> ResolveSemesterIdAsync(ApplicationDbContext db, DateOnly date, CancellationToken ct = default) =>
        await db.SemesterPeriods.AsNoTracking()
            .Where(p => p.StartDate <= date && p.EndDate >= date)
            .OrderBy(p => p.Id)
            .Select(p => (int?)p.Id)
            .FirstOrDefaultAsync(ct);

    /// <summary>(user, 활동 학기) 매핑 행을 보장한다 — 없으면 계정 라벨로 만든다. 저장은 호출자가 한다.</summary>
    public static async Task<SemesterMember> EnsureUserRowAsync(
        ApplicationDbContext db, string userId, Activity activity, DateTime now, CancellationToken ct = default)
    {
        var semesterId = await ResolveSemesterIdAsync(db, activity.ActivityDate, ct);
        var existing = await db.SemesterMembers
            .FirstOrDefaultAsync(m => m.MappedUserId == userId && m.SemesterId == semesterId, ct);
        if (existing is not null) return existing;

        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, ct);
        var row = new SemesterMember
        {
            SemesterId = semesterId,
            Label = UserLabel(user),
            Name = user?.Name.Trim() ?? "",
            MappedUserId = userId,
            CreatedAt = now,
        };
        db.SemesterMembers.Add(row);
        return row;
    }

    /// <summary>계정 파생 행 라벨 — 내보내기 행 라벨 형식('이름 학번 학과')과 동일.</summary>
    public static string UserLabel(ApplicationUser? user) =>
        user is null ? "" : $"{user.Name.Trim()} {user.StudentNumber.Trim()} {user.Department.Trim()}".Trim();
}
