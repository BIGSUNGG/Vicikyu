namespace VolleyballClub.Application.Dtos;

/// <summary>활동 기록 xlsx 가져오기 결과 요약.</summary>
public record ActivityImportReport(
    int ImportedActivities,
    int ImportedAttendances,
    int DayMarks,
    int UnmappedMembers,
    int UnmappedAttendances,
    IReadOnlyList<string> Warnings);

/// <summary>미매핑 부원 목록 행 — 아직 실제 계정과 연결되지 않은 xlsx 행 라벨.</summary>
public record UnmappedMemberDto(int Id, string Label, string Name, int AttendanceCount, DateTime CreatedAt);
