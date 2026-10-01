namespace VolleyballClub.Domain;

/// <summary>한국 표준시(KST) 기준 시각 유틸. 서버 시간대와 무관하게 '오늘'을 KST로 판정한다.</summary>
public static class KstClock
{
    private static readonly TimeZoneInfo Kst = Resolve();

    private static TimeZoneInfo Resolve()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time"); }
    }

    public static DateTime NowUtc(TimeProvider timeProvider) => timeProvider.GetUtcNow().UtcDateTime;

    public static DateOnly Today(TimeProvider timeProvider) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(NowUtc(timeProvider), Kst));

    /// <summary>KST 현지 시각 문자열(HH:mm)로 표시용 변환</summary>
    public static string ToKstTimeString(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Kst).ToString("HH:mm");

    public static string ToKstDateString(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Kst).ToString("yyyy.MM.dd");
}
