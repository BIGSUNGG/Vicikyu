namespace VolleyballClub.Domain.Entities;

/// <summary>팀원. UserId 또는 GuestId 중 정확히 하나만 값이 있다(체크 제약).</summary>
public class TeamMember
{
    public int Id { get; set; }

    public int TeamId { get; set; }

    public Team Team { get; set; } = null!;

    public string? UserId { get; set; }

    public int? GuestId { get; set; }

    public DateTime CreatedAt { get; set; }
}
