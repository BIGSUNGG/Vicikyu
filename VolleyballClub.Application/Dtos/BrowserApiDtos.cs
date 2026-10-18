namespace VolleyballClub.Application.Dtos;

public record SessionUserDto(string UserId, string Name, string[] Roles);
public record CsrfTokenDto(string Token);
public record LoginRequest(string StudentNumber, string Password);
public record ApiErrorDto(string Kind, string Message, string? CorrelationId = null);
public record TextRequest(string Value);
public record AttendanceChangeRequest(bool ConfirmTeamImpact = false);
public record TeamMoveRequest(int TargetTeamId);
public record TeamAddRequest(string ParticipantKey, int TeamId);
public record FeePaymentRequest(bool IsPaid, string? Memo = null);
public record SheetSaveRequest(int? Id, string Name, string SpreadsheetIdOrUrl);
public record SheetMappingRequest(int SheetId, string TabName, string? RowLabel, bool AddNewRow = false);
public record SheetBulkOptionsRequest(IReadOnlyList<string> Names);
