using VolleyballClub.Domain.Enums;

namespace VolleyballClub.Domain.Entities;

/// <summary>학기별 부원 행의 출석 기록 — OTP·관리자·가져오기 출석의 단일 저장소.
/// 통계·랭킹은 SemesterMember.MappedUserId 조인으로 집계한다.</summary>
public class SemesterAttendance
{
    public int Id { get; set; }

    public int SemesterMemberId { get; set; }

    public SemesterMember SemesterMember { get; set; } = null!;

    public int ActivityId { get; set; }

    public Activity Activity { get; set; } = null!;

    public AttendanceType AttendanceType { get; set; }

    public DateTime CheckedAt { get; set; }

    public DateTime CreatedAt { get; set; }
}
