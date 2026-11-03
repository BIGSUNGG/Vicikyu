using VolleyballClub.Domain.Enums;
namespace VolleyballClub.Application.Dtos;

/// <summary>관리자 활동 목록 행 — DayMark(예정/없음)와 Activity(완료/진행)를 한 목록으로 보여준다.
/// Key: DayMark는 "mark:{yyyy-MM-dd}", Activity는 "app:{id}". Label은 라벨 활동 이름(예: 'OT').</summary>
public record ManagedActivityDto(string Key, int? ActivityId, DateOnly Date, string? Label,
    DayKind Kind, string? Note, ActivityStatus? Status, string StateText, bool CanEditAttendance, int AttendanceCount);

/// <summary>활동 생성 — Scheduled/NoActivity는 DayMark로, Completed는 (라벨) 종료 활동으로 만든다.</summary>
public record ActivityCreateRequest(DateOnly Date, DayKind Kind, string? Label = null, string? Note = null);

// Null Kind preserves the current kind. Attendances(선택)는 변경 저장 1회로 활동 정보와 출석 변경을 원자 적용한다.
public record ActivityEditRequest(DateOnly Date, DayKind? Kind = null, string? Label = null, string? Note = null,
    IReadOnlyList<ActivityAttendanceSetRequest>? Attendances = null);
public record ActivityAttendanceSetRequest(string UserId, bool IsAttended);
public record ActivityAttendanceRequest(bool IsAttended, bool ConfirmTeamImpact = false);
public record MemberActivityRowDto(string Key, int? ActivityId, DateOnly Date, string? Label,
    string StateText, bool IsAttended, bool CanEditAttendance, bool HasTeam);
public record MemberActivityListDto(string UserId, int MonthCount, int SemesterCount, int TotalCount,
    IReadOnlyList<MemberActivityRowDto> Items);
