namespace VolleyballClub.Domain.Entities;

/// <summary>회비 납부 기간(예: 2026년 2학기).</summary>
public class FeePeriod
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    public DateOnly StartDate { get; set; }

    public DateOnly EndDate { get; set; }

    public int FeeAmount { get; set; }

    public DateTime CreatedAt { get; set; }

    public List<FeePayment> Payments { get; set; } = [];
}
