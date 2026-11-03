namespace VolleyballClub.Domain.Enums;

/// <summary>날짜 표시 종류. Completed는 xlsx 파싱 결과에만 쓰이고 DB(DayMark)에는 Scheduled/NoActivity만 저장된다.</summary>
public enum DayKind
{
    Completed = 0,
    Scheduled = 1,
    NoActivity = 2,
}
