namespace VolleyballClub.Application.Dtos;

public record FeePeriodDto(
    int Id,
    string Name,
    DateOnly StartDate,
    DateOnly EndDate,
    int FeeAmount);

public record FeePeriodCreateRequest(
    string Name,
    DateOnly StartDate,
    DateOnly EndDate,
    int FeeAmount);

public record FeeStatusDto(
    int PeriodId,
    string PeriodName,
    string UserId,
    string Name,
    string StudentNumber,
    string Department,
    bool IsPaid,
    DateTime? PaidAt,
    string? Memo);
