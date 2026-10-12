using Microsoft.EntityFrameworkCore;
using VolleyballClub.Domain.Entities;

namespace VolleyballClub.Infrastructure.Persistence;

/// <summary>
/// '현재 학기' 선택 규칙 — 오늘을 포함하는 학기 → 없으면 시작일이 가장 최근인 학기 →
/// 학기가 전혀 없으면 고정 기본 규칙(1학기 3/1~8/31, 2학기 9/1 시작 — 9~12월 기준일은 12/31, 1~2월 기준일은 익년 2월 말 종료).
/// 기본 규칙은 학기 관리에서 아직 아무 학기도 만들지 않은 초기 상태의 폴백이다.
/// </summary>
public static class SemesterPeriodResolver
{
    public static async Task<(DateOnly Start, DateOnly End)> GetCurrentRangeAsync(
        ApplicationDbContext db, DateOnly today)
    {
        var period = await GetCurrentAsync(db, today);
        return period is null ? DefaultRange(today) : (period.StartDate, period.EndDate);
    }

    /// <summary>오늘 포함 학기 → 없으면 시작일 최근 학기. 학기 행이 없으면 null.</summary>
    public static async Task<SemesterPeriod?> GetCurrentAsync(ApplicationDbContext db, DateOnly today)
    {
        var period = await FindCurrentAsync(db, today);
        if (period is not null)
        {
            return period;
        }

        return await db.SemesterPeriods.AsNoTracking()
            .OrderByDescending(p => p.StartDate)
            .FirstOrDefaultAsync();
    }

    public static Task<SemesterPeriod?> FindCurrentAsync(ApplicationDbContext db, DateOnly today) =>
        db.SemesterPeriods.AsNoTracking()
            .Where(p => p.StartDate <= today && p.EndDate >= today)
            .OrderByDescending(p => p.StartDate)
            .FirstOrDefaultAsync();

    public static (DateOnly Start, DateOnly End) DefaultRange(DateOnly date)
    {
        if (date.Month >= 3 && date.Month <= 8)
        {
            return (new DateOnly(date.Year, 3, 1), new DateOnly(date.Year, 8, 31));
        }

        if (date.Month >= 9)
        {
            return (new DateOnly(date.Year, 9, 1), new DateOnly(date.Year, 12, 31));
        }

        // 1~2월은 직전 학년도 2학기로 본다
        return (new DateOnly(date.Year - 1, 9, 1), new DateOnly(date.Year, 2, DateTime.DaysInMonth(date.Year, 2)));
    }

    /// <summary>기본 규칙 기준 학기 표기(예: "2026년 1학기") — 초기 시드에서만 사용.</summary>
    public static string DefaultLabel(DateOnly date)
    {
        var (start, _) = DefaultRange(date);
        return $"{start.Year}년 {(start.Month == 3 ? "1" : "2")}학기";
    }
}
