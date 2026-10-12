namespace VolleyballClub.Domain.Entities;

public class Team
{
    public int Id { get; set; }

    public int ActivityId { get; set; }

    public Activity Activity { get; set; } = null!;

    public string Name { get; set; } = "";

    public int Order { get; set; }

    public DateTime CreatedAt { get; set; }

    public List<TeamMember> Members { get; set; } = [];
}
