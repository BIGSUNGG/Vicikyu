namespace VolleyballClub.Domain.Entities;

/// <summary>당일 임시 참가자. Users 테이블에 등록되지 않고 해당 Activity에만 연결된다.</summary>
public class Guest
{
    public int Id { get; set; }

    public int ActivityId { get; set; }

    public Activity Activity { get; set; } = null!;

    public string Name { get; set; } = "";

    public DateTime CreatedAt { get; set; }
}
