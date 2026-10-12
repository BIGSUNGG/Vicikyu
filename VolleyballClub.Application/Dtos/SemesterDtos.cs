namespace VolleyballClub.Application.Dtos;

public record SemesterDto(
    int Id,
    string Name,
    DateOnly StartDate,
    DateOnly EndDate,
    bool IsCurrent);

public record SemesterSaveRequest(
    string Name,
    DateOnly StartDate,
    DateOnly EndDate);
