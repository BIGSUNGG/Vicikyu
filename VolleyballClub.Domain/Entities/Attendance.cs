using VolleyballClub.Domain.Enums;

namespace VolleyballClub.Domain.Entities;

/// <summary>출석 기록. (ActivityId, UserId) 복합 Unique로 중복 출석을 DB에서 차단한다.</summary>
public class Attendance
{
    public int Id { get; set; }

    public int ActivityId { get; set; }

    public Activity Activity { get; set; } = null!;

    public string UserId { get; set; } = "";

    public AttendanceType AttendanceType { get; set; }

    public DateTime CheckedAt { get; set; }

    public DateTime CreatedAt { get; set; }
}
