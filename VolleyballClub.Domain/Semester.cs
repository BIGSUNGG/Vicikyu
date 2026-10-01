namespace VolleyballClub.Domain;

/// <summary>한국 대학 학기 구간. 1학기 3/1~8/31(여름 포함), 2학기 9/1~익년 2/말(겨울 포함).</summary>
public static class Semester
{
    public static (DateOnly Start, DateOnly End) Of(DateOnly date)
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

    public static (DateOnly Start, DateOnly End) Of(TimeProvider timeProvider) => Of(KstClock.Today(timeProvider));

    public static string Label(DateOnly date)
    {
        var (start, _) = Of(date);
        var semester = start.Month == 3 ? "1학기" : "2학기";
        return $"{start.Year}년 {semester}";
    }
}
