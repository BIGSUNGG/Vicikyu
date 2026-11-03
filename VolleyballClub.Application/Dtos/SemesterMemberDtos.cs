namespace VolleyballClub.Application.Dtos;

/// <summary>학기별 부원 행 목록 항목 — 매핑 여부·출석 수 포함.</summary>
public record SemesterMemberRowDto(
    int Id,
    int? SemesterId,
    string? SemesterName,
    string Label,
    string Name,
    string? MappedUserId,
    string? MappedUserName,
    int AttendanceCount,
    DateTime CreatedAt);

/// <summary>학기별 부원 행의 출석 편집 화면 — 해당 학기(또는 행의 출석 날짜) 활동별 출석 여부.</summary>
public record SemesterRowAttendanceDto(
    int RowId,
    string Label,
    string? SemesterName,
    IReadOnlyList<SemesterRowActivityDto> Activities);

public record SemesterRowActivityDto(int ActivityId, DateOnly Date, string? Label, bool Attended);

/// <summary>부원 상세 '시트 연결' 탭 — 학기별 현재 매핑 행과 후보(미매핑) 행.</summary>
public record MemberSheetRowsDto(
    string UserId,
    string MemberName,
    IReadOnlyList<MemberSemesterSheetDto> Semesters);

public record MemberSemesterSheetDto(
    int? SemesterId,
    string SemesterName,
    int? MappedRowId,
    string? MappedRowLabel,
    int AttendanceCount,
    IReadOnlyList<SemesterMemberRowDto> Candidates);

/// <summary>행 추가·매핑 요청.</summary>
public record SemesterMemberCreateRequest(int SemesterId, string Name, string? Label);

public record SemesterMemberMappingRequest(string? UserId);
