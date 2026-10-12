namespace VolleyballClub.Domain.Entities;

/// <summary>한국 대학 학기 구간(예: 2026년 1학기 3/1~8/31). 출석 통계·랭킹의 기간 기준.</summary>
public class SemesterPeriod
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    public DateOnly StartDate { get; set; }

    public DateOnly EndDate { get; set; }

    public DateTime CreatedAt { get; set; }
}
