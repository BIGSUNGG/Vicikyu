namespace VolleyballClub.Domain.Entities;

/// <summary>미매핑 부원의 출석 기록 — 부원 계정과 연동되면 Attendance로 이관된다.
/// 일반 출석 통계·랭킹에는 포함되지 않는다(아직 부원이 아니므로).</summary>
public class UnmappedAttendance
{
    public int Id { get; set; }

    public int UnmappedMemberId { get; set; }

    public UnmappedMember UnmappedMember { get; set; } = null!;

    public int ActivityId { get; set; }

    public Activity Activity { get; set; } = null!;

    public DateTime CreatedAt { get; set; }
}
