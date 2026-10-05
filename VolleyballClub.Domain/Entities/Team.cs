namespace VolleyballClub.Domain.Entities;

public class Team
{
    public int Id { get; set; }

    public int ActivityId { get; set; }

    public Activity Activity { get; set; } = null!;

    public string Name { get; set; } = "";

    public int Order { get; set; }

    /// <summary>같은 날 여러 번 편성할 수 있으므로 활동 내 회차 번호(1부터 시작).</summary>
    public int DrawNumber { get; set; } = 1;

    public DateTime CreatedAt { get; set; }

    public List<TeamMember> Members { get; set; } = [];
}
