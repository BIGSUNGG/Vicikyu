using VolleyballClub.Domain.Enums;

namespace VolleyballClub.Domain.Entities;

/// <summary>시트에 활동 예정/활동 없음으로 표시된 날 — 캘린더 표시용 투영.
/// 활동 완료 날은 실제 Activity(+Attendance)로 가져오므로 마크가 생기지 않고,
/// 시트에서 완료로 바뀌면 동기화가 마크를 지운다. Date가 하루 1개(단일 출석부 가정)이므로 PK.</summary>
public class SheetDayMark
{
    public DateOnly Date { get; set; }

    public SheetDayKind Kind { get; set; }
}
