using System.Collections.Concurrent;
using VolleyballClub.Application.Dtos;

namespace VolleyballClub.Infrastructure.Services;

/// <summary>프로세스 내 ISheetTabCache — web 인스턴스가 항상 단일(개발 1·배포 1 컨테이너)이므로
/// 외부 캐시 서버 없이 동작한다. TTL은 시트를 시트 UI 등 앱 밖에서 직접 편집했을 때의 최대 반영
/// 지연 상한(5분)이며, 앱 내 쓰기는 CachedSheetClient가 즉시 무효화한다.
/// 캐시 특성상 재시작 시 유지할 필요가 없다.</summary>
public sealed class MemorySheetTabCache : ISheetTabCache
{
    private sealed record Entry(IReadOnlyList<SheetTab> Tabs, DateTimeOffset ExpiresAt);

    private readonly ConcurrentDictionary<string, Entry> store = [];

    /// <summary>외부 직접 편집 반영 지연 상한.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    public Task<IReadOnlyList<SheetTab>?> GetAsync(string spreadsheetId, CancellationToken ct = default)
    {
        if (store.TryGetValue(spreadsheetId, out var entry) && entry.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return Task.FromResult<IReadOnlyList<SheetTab>?>(entry.Tabs);
        }

        store.TryRemove(spreadsheetId, out _); // 만료 잔여 제거
        return Task.FromResult<IReadOnlyList<SheetTab>?>(null);
    }

    public Task SetAsync(string spreadsheetId, IReadOnlyList<SheetTab> tabs, CancellationToken ct = default)
    {
        store[spreadsheetId] = new Entry(tabs, DateTimeOffset.UtcNow + Ttl);
        return Task.CompletedTask;
    }

    public Task InvalidateAsync(string spreadsheetId, CancellationToken ct = default)
    {
        store.TryRemove(spreadsheetId, out _);
        return Task.CompletedTask;
    }
}
