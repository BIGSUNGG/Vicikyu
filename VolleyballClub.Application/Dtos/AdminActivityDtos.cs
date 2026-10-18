using VolleyballClub.Domain.Enums;
namespace VolleyballClub.Application.Dtos;

public record ActivityTargetDto(int SheetId, string SheetName, string TabName, DateOnly From, DateOnly To);
public record ManagedActivityDto(string Key, int? ActivityId, int? SheetId, string? TabName,
    DateOnly Date, string SourceLabel, SheetDayKind Kind, string? Reason,
    ActivityStatus? Status, string StateText, bool CanEditAttendance, int AttendanceCount);
public record ActivityCreateRequest(int SheetId, string TabName, DateOnly Date, SheetDayKind Kind, string? Reason = null);
// Null Kind preserves the operational state as well as the source kind.
public record ActivityEditRequest(DateOnly Date, SheetDayKind? Kind = null, string? Reason = null, int? SheetId = null, string? TabName = null);
public record ActivityAttendanceRequest(bool IsAttended, bool ConfirmTeamImpact = false);
public record MemberActivityRowDto(string Key, int? ActivityId, DateOnly Date, string SourceLabel,
    string StateText, bool IsAttended, bool CanEditAttendance, bool HasTeam);
public record MemberActivityListDto(string UserId, int MonthCount, int SemesterCount, int TotalCount,
    IReadOnlyList<MemberActivityRowDto> Items);

/// <summary>Raw userEnteredValue JSON preserves formula/string/number/bool distinctions.</summary>
public record ManagedSheetTab(string Name, IReadOnlyList<IReadOnlyList<string>> Rows,
    IReadOnlyList<IReadOnlyList<string>> RawValues, IReadOnlyDictionary<int, string> ColumnKeys,
    string? LastMutation = null, IReadOnlyList<IReadOnlyList<string>>? RawCellData = null,
    IReadOnlyList<string>? ColumnProperties = null, IReadOnlyList<string>? OtherColumnMetadata = null, IReadOnlyList<string>? RowProperties = null);
public enum SheetMutationKind { Bind, Insert, Write, Delete, InsertMemberRow }
public record ManagedSheetMutation(string OperationId, SheetMutationKind Kind, int ColumnIndex,
    string SourceKey, IReadOnlyList<string> RawValues, int RowIndex = 0);
