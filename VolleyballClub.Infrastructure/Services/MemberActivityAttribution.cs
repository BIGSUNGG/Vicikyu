using Microsoft.EntityFrameworkCore;
using VolleyballClub.Application.Abstractions;
using VolleyballClub.Infrastructure.Persistence;

namespace VolleyballClub.Infrastructure.Services;

/// <summary>활동의 부원 귀속 판정 — 부원이 '그 학기에 부원이었다'의 유일한 근거는 시트 탭 매핑이다.
/// 앱 활동도 시트 가져오기 활동과 같은 규칙을 적용한다(계정 생성일 JoinedAt은 사용하지 않는다).
/// 활동 날짜가 어느 학기 탭에도 속하지 않으면(방학·시트 미등록) 아무 부원에게도 귀속하지 않는다(2026-10-19 확정).</summary>
internal static class MemberActivityAttribution
{
    /// <summary>이 부원이 시트 탭에 매핑된 학기 기간 목록 — 매핑은 곧 '그 학기에 부원이었다'의 판별 근거.</summary>
    public static async Task<List<(DateOnly From, DateOnly To)>> GetMappedPeriodsAsync(
        ApplicationDbContext db, ISheetFormatResolver resolver, string userId, CancellationToken ct = default)
    {
        var tabNames = await db.SheetMemberMappings.AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => m.TabName)
            .ToListAsync(ct);

        return tabNames
            .Select(resolver.ResolveByTabName)
            .Where(format => format is not null)
            .Select(format => (format!.From, format.To))
            .ToList();
    }

    /// <summary>활동이 이 부원에게 귀속되는가 — 활동 날짜가 매핑 학기 기간 안이면 귀속.</summary>
    public static bool IsAttributed(DateOnly activityDate, List<(DateOnly From, DateOnly To)> mappedPeriods) =>
        mappedPeriods.Any(p => activityDate >= p.From && activityDate <= p.To);
}
