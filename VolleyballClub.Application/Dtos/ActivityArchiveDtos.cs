namespace VolleyballClub.Application.Dtos;

/// <summary>활동 기록 xlsx 가져오기 결과 요약.</summary>
public record ActivityImportReport(
    int ImportedActivities,
    int ImportedAttendances,
    int DayMarks,
    int UnmappedMembers,
    int UnmappedAttendances,
    IReadOnlyList<string> Warnings);
