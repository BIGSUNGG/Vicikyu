namespace VolleyballClub.Application.Abstractions;

/// <summary>시트 행 후보 — Recommended가 true면 이름 추천(드롭다운 최상단 강조) 대상.</summary>
public sealed record SheetRowOptionDto(string RowLabel, bool Recommended);

/// <summary>한 부원의 탭별 시트 행 연결 상태. CurrentRowLabel이 null이면 미지정.</summary>
public sealed record MemberTabMappingDto(
    int SheetId,
    string SheetName,
    string TabName,
    string? CurrentRowLabel,
    IReadOnlyList<SheetRowOptionDto> Options);

public interface ISheetMemberMappingService
{
    /// <summary>부원 하나의 탭별 연결 상태 + 행 라벨 후보(추천 순 정렬). 시트를 실시간으로 읽는다.</summary>
    Task<List<MemberTabMappingDto>> GetForMemberAsync(string userId);

    /// <summary>아직 계정이 없는 일괄 추가 행용 — 이름별 탭 행 후보(추천 순). 시트는 한 번만 읽는다.
    /// 다른 계정에 이미 연결된 행은 후보에서 제외되고 CurrentRowLabel은 항상 null이다.</summary>
    Task<IReadOnlyDictionary<string, List<MemberTabMappingDto>>> GetBulkOptionsAsync(IReadOnlyList<string> names);

    /// <summary>선택한 탭만 연결/해제한다. addNewRow는 빈 부원 행을 먼저 검증·삽입한다.
    /// 출석 가져오기/내보내기는 실행하지 않는다.</summary>
    Task SetMappingAsync(int sheetId, string tabName, string userId, string? rowLabel, bool addNewRow = false);

    /// <summary>현재 학기 탭(오늘 활동을 기록하는 탭)에 매핑이 없는 부원 UserId 집합 — 활성/비활성 무관.
    /// 시트를 읽지 않고 DB만 조회한다. 현재 학기 전략이 없거나 출석 시트가 등록되지 않았으면 빈 집합.</summary>
    Task<IReadOnlySet<string>> GetUnmappedUserIdsAsync();
}
