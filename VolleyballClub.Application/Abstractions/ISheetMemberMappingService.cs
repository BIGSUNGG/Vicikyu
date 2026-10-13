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
}
