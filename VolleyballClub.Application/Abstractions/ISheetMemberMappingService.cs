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

    /// <summary>탭별 연결 저장. rowLabel이 null이면 미지정으로 해제한다.
    /// 저장 후 즉시 동기화를 실행하고 결과를 반환한다(실패 시 null — 저장 자체는 유지, 다음 기동에 반영).</summary>
    Task<SheetSyncReport?> SetMappingAsync(int sheetId, string tabName, string userId, string? rowLabel);

    /// <summary>현재 학기 탭(오늘 활동을 기록하는 탭)에 매핑이 없는 부원 UserId 집합 — 활성/비활성 무관.
    /// 시트를 읽지 않고 DB만 조회한다. 현재 학기 전략이 없거나 출석 시트가 등록되지 않았으면 빈 집합.</summary>
    Task<IReadOnlySet<string>> GetUnmappedUserIdsAsync();
}
