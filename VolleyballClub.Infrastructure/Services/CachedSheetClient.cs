using Microsoft.Extensions.Logging;
using VolleyballClub.Application.Abstractions;
using VolleyballClub.Application.Dtos;

namespace VolleyballClub.Infrastructure.Services;

/// <summary>ISheetClient 데코레이터 — 전체 탭 원본 읽기(GetTabsAsync, tabNames=null)를 캐시한다.
/// 활동 관리 페이지·부원 상세 시트 연결·동기화 등 모든 전체 읽기가 이곳을 경유해 원격 Google Sheets
/// API 왕복을 건너뛴다. 쓰기(열 기록/삽입/삭제, 관리형 변경·복원) 성공 직후 해당 스프레드시트 캐시를
/// 즉시 무효화한다. 캐시 저장소 오류는 기능을 해치지 않도록 경고만 남기고 원본으로 우회한다.</summary>
public sealed class CachedSheetClient(
    ISheetClient inner,
    ISheetTabCache cache,
    ILogger<CachedSheetClient> logger) : ISheetClient
{
    public async Task<IReadOnlyList<SheetTab>> GetTabsAsync(
        string spreadsheetId, IEnumerable<string>? tabNames = null, CancellationToken ct = default)
    {
        // 부분 탭 조회는 전체 탭 캐시와 범위가 달라 우회한다 — 현재 모든 호출처는 전체 조회만 사용한다.
        if (tabNames is not null)
        {
            return await inner.GetTabsAsync(spreadsheetId, tabNames, ct);
        }

        try
        {
            if (await cache.GetAsync(spreadsheetId, ct) is { } cached)
            {
                return cached;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "시트 탭 캐시 조회 실패 — 원격 조회로 진행: {SpreadsheetId}", spreadsheetId);
        }

        var tabs = await inner.GetTabsAsync(spreadsheetId, tabNames, ct);
        try
        {
            await cache.SetAsync(spreadsheetId, tabs, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "시트 탭 캐시 저장 실패(무시): {SpreadsheetId}", spreadsheetId);
        }
        return tabs;
    }

    public async Task WriteColumnAsync(
        string spreadsheetId, string tabName, int columnIndex, SheetColumn column, CancellationToken ct = default)
    {
        await inner.WriteColumnAsync(spreadsheetId, tabName, columnIndex, column, ct);
        await InvalidateQuietlyAsync(spreadsheetId, ct);
    }

    public async Task InsertColumnAsync(
        string spreadsheetId, string tabName, int columnIndex, SheetColumn column, CancellationToken ct = default)
    {
        await inner.InsertColumnAsync(spreadsheetId, tabName, columnIndex, column, ct);
        await InvalidateQuietlyAsync(spreadsheetId, ct);
    }

    public async Task DeleteColumnAsync(string spreadsheetId, string tabName, int columnIndex, CancellationToken ct = default)
    {
        await inner.DeleteColumnAsync(spreadsheetId, tabName, columnIndex, ct);
        await InvalidateQuietlyAsync(spreadsheetId, ct);
    }

    /// <summary>관리형 단일 탭 원본(그리드 서식 포함) 읽기 — 변경 검증 흐름 안에서 즉시 쓰기에 쓰이므로 캐시하지 않는다.</summary>
    public Task<ManagedSheetTab> GetManagedTabAsync(string spreadsheetId, string tabName, CancellationToken ct = default) =>
        inner.GetManagedTabAsync(spreadsheetId, tabName, ct);

    public async Task ApplyManagedMutationAsync(
        string spreadsheetId, ManagedSheetTab expected, ManagedSheetMutation mutation, CancellationToken ct = default)
    {
        await inner.ApplyManagedMutationAsync(spreadsheetId, expected, mutation, ct);
        await InvalidateQuietlyAsync(spreadsheetId, ct);
    }

    public async Task RestoreManagedTabAsync(
        string spreadsheetId, ManagedSheetTab before, ManagedSheetTab expected, ManagedSheetMutation mutation, CancellationToken ct = default)
    {
        await inner.RestoreManagedTabAsync(spreadsheetId, before, expected, mutation, ct);
        await InvalidateQuietlyAsync(spreadsheetId, ct);
    }

    /// <summary>쓰기는 이미 원본에 반영된 상태 — 무효화 실패는 TTL 만료로 자연 해소되므로 쓰기 자체를 실패시키지 않는다.</summary>
    private async Task InvalidateQuietlyAsync(string spreadsheetId, CancellationToken ct)
    {
        try
        {
            await cache.InvalidateAsync(spreadsheetId, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "시트 탭 캐시 무효화 실패 — TTL({Ttl}) 후 자동 만료: {SpreadsheetId}",
                RedisSheetTabCache.Ttl, spreadsheetId);
        }
    }
}
