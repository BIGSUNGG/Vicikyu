namespace VolleyballClub.Domain.Entities;

/// <summary>활동 예정/활동 없음으로 관리자가 지정한 날 — 캘린더 표시용.
/// 활동 완료 날은 실제 Activity(+Attendance)로 표시되므로 마크가 생기지 않는다.
/// 해당 날짜에 활동이 만들어지면(시작·생성) 예정 마크는 제거된다. Date가 하루 1개이므로 PK.</summary>
public class DayMark
{
    public DateOnly Date { get; set; }

    public Domain.Enums.DayKind Kind { get; set; }

    /// <summary>활동 없음 사유(예: '추석 연휴'). null이면 사유 없음.</summary>
    public string? Note { get; set; }
}
