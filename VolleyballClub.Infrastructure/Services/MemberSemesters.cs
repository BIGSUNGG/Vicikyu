using Microsoft.EntityFrameworkCore;
using VolleyballClub.Infrastructure.Persistence;

namespace VolleyballClub.Infrastructure.Services;

/// <summary>부원의 학기 귀속 판정(DB 기준) — 그 학기에 매핑된 학기별 행이 있거나 가입일(JoinedAt)이 학기 안이면
/// '그 학기에 부원이었다'로 본다(가입일은 늦게 만든 계정 보정용).</summary>
internal static class MemberSemesters
{
    public static async Task<List<(DateOnly From, DateOnly To)>> GetAttributedPeriodsAsync(
        ApplicationDbContext db, string userId, CancellationToken ct = default)
    {
        var periods = await db.SemesterPeriods.AsNoTracking().OrderBy(p => p.StartDate).ToListAsync(ct);
        if (periods.Count == 0) return [];
        var joinedUtc = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => (DateTime?)u.JoinedAt).FirstOrDefaultAsync(ct);
        if (joinedUtc is null) return [];
        var kst = TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul");
        var joined = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(joinedUtc.Value, DateTimeKind.Utc), kst));
        var mappedSemesterIds = await db.SemesterMembers.AsNoTracking()
            .Where(m => m.MappedUserId == userId && m.SemesterId != null)
            .Select(m => m.SemesterId!.Value)
            .ToListAsync(ct);
        var mapped = mappedSemesterIds.ToHashSet();
        return periods
            .Where(p => (joined >= p.StartDate && joined <= p.EndDate) || mapped.Contains(p.Id))
            .Select(p => (p.StartDate, p.EndDate))
            .ToList();
    }

    public static bool IsAttributed(DateOnly activityDate, List<(DateOnly From, DateOnly To)> periods) =>
        periods.Any(p => activityDate >= p.From && activityDate <= p.To);
}
