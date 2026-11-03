namespace VolleyballClub.Domain.Entities;

/// <summary>xlsx 가져오기에서 DB 부원과 매칭되지 않은 행 라벨의 임시 보관소.
/// 관리자가 실제 부원 계정과 연동(Link)하면 출석 기록이 해당 계정으로 이관되고 이 행은 삭제된다.</summary>
public class UnmappedMember
{
    public int Id { get; set; }

    /// <summary>xlsx 원본 행 라벨 전문(예: '박유진 24 만화'). 재업로드 시 같은 라벨끼리 묶는 키.</summary>
    public string Label { get; set; } = "";

    /// <summary>라벨에서 파싱한 이름(표시용).</summary>
    public string Name { get; set; } = "";

    public DateTime CreatedAt { get; set; }

    public List<UnmappedAttendance> Attendances { get; set; } = [];
}
