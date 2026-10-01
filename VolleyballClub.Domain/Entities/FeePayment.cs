namespace VolleyballClub.Domain.Entities;

/// <summary>회비 납부 여부. (FeePeriodId, UserId) 복합 Unique.</summary>
public class FeePayment
{
    public int Id { get; set; }

    public int FeePeriodId { get; set; }

    public FeePeriod FeePeriod { get; set; } = null!;

    public string UserId { get; set; } = "";

    public bool IsPaid { get; set; }

    public DateTime? PaidAt { get; set; }

    public string? Memo { get; set; }

    public string UpdatedByUserId { get; set; } = "";

    public DateTime UpdatedAt { get; set; }
}
