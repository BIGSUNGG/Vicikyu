using VolleyballClub.Application.Dtos;

namespace VolleyballClub.Application.Abstractions;

/// <summary>학기별 부원 행 관리(관리자) — 행 추가·제거·계정 매핑·출석 편집.
/// 행은 영구 보존되며 매핑은 연결만 바꾼다(삭제 아님).</summary>
public interface ISemesterMemberService
{
    /// <summary>학기별 부원 행 목록. semesterId=null이면 전체(미분류 포함), mapped=true/false로 필터.</summary>
    Task<IReadOnlyList<SemesterMemberRowDto>> GetListAsync(int? semesterId = null, bool? mapped = null, CancellationToken ct = default);

    /// <summary>행을 계정에 매핑(userId=null이면 해제). 행과 출석은 유지된다.</summary>
    Task SetMappingAsync(int rowId, string? userId, CancellationToken ct = default);

    Task<SemesterMemberRowDto> AddRowAsync(SemesterMemberCreateRequest request, CancellationToken ct = default);

    /// <summary>행과 그 출석 기록을 삭제한다.</summary>
    Task RemoveRowAsync(int rowId, CancellationToken ct = default);

    /// <summary>행의 활동별 출석 목록(편집 화면).</summary>
    Task<SemesterRowAttendanceDto> GetRowAttendanceAsync(int rowId, CancellationToken ct = default);

    /// <summary>행의 활동 출석을 설정/해제한다.</summary>
    Task SetRowAttendanceAsync(int rowId, int activityId, bool attended, CancellationToken ct = default);

    /// <summary>부원 상세 '시트 연결' 탭 데이터 — 학기별 매핑 행·후보.</summary>
    Task<MemberSheetRowsDto> GetForMemberAsync(string userId, CancellationToken ct = default);
}
