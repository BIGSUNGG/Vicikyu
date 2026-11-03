namespace VolleyballClub.Domain.Entities;

/// <summary>학기별 부원 행 — xlsx 가져오기·라이브 출석의 영구 원천. 한 계정은 학기마다 행을 가진다(1:N).
/// 매핑(MappedUserId)은 행을 삭제하지 않고 연결만 바꾼다.</summary>
public class SemesterMember
{
    public int Id { get; set; }

    /// <summary>행이 속한 학기. null이면 어느 학기에도 속하지 않은 기록(미분류).</summary>
    public int? SemesterId { get; set; }

    public SemesterPeriod? Semester { get; set; }

    /// <summary>원본 행 라벨 전문(예: '박유진 24 만화'). 계정에서 파생한 행은 '이름 학번 학과' 합성 라벨.</summary>
    public string Label { get; set; } = "";

    /// <summary>라벨에서 파싱한 이름(표시용).</summary>
    public string Name { get; set; } = "";

    /// <summary>매핑된 부원 계정 ID. null이면 아직 미매핑 — 출석 통계에 포함되지 않는다.</summary>
    public string? MappedUserId { get; set; }

    public DateTime CreatedAt { get; set; }

    public List<SemesterAttendance> Attendances { get; set; } = [];
}
