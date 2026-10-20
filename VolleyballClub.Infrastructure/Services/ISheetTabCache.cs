using VolleyballClub.Application.Dtos;

namespace VolleyballClub.Infrastructure.Services;

/// <summary>시트 탭 원본 전체 읽기(ISheetClient.GetTabsAsync, tabNames=null) 결과의 캐시 저장소.
/// CachedSheetClient가 사용하고 메모리 구현은 MemorySheetTabCache. 원격 Google Sheets API 지연을
/// 흡수하기 위한 투명 최적화 계층이다 — 캐시 오류는 읽기·쓰기 기능을 해쳐서는 안 된다.</summary>
public interface ISheetTabCache
{
    /// <summary>캐시된 전체 탭 목록. 없거나 역직렬화 불가능하면 null(미스).</summary>
    Task<IReadOnlyList<SheetTab>?> GetAsync(string spreadsheetId, CancellationToken ct = default);

    /// <summary>전체 탭 목록을 TTL과 함께 저장한다.</summary>
    Task SetAsync(string spreadsheetId, IReadOnlyList<SheetTab> tabs, CancellationToken ct = default);

    /// <summary>해당 스프레드시트의 캐시를 지운다 — 앱 내 시트 쓰기 성공 직후 호출.</summary>
    Task InvalidateAsync(string spreadsheetId, CancellationToken ct = default);
}
