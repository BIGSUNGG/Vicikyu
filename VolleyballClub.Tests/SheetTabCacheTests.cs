using Microsoft.Extensions.Logging.Abstractions;
using VolleyballClub.Application.Abstractions;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Infrastructure.Services;

namespace VolleyballClub.Tests;

/// <summary>CachedSheetClient(시트 탭 읽기 Redis 캐시 데코레이터)의 히트/미스/무효화/오류 우회 논리 검증.
/// 라이브 Redis 없이 ISheetTabCache 페이크로 검증한다.</summary>
public sealed class SheetTabCacheTests
{
    private static readonly IReadOnlyList<SheetTab> Tabs =
        [new("2026-2", (IReadOnlyList<IReadOnlyList<string>>)[["날짜"], ["김민수"]])];

    private sealed class FakeCache : ISheetTabCache
    {
        private readonly Dictionary<string, IReadOnlyList<SheetTab>> store = [];
        public int Gets, Sets, Invalidates;

        public Task<IReadOnlyList<SheetTab>?> GetAsync(string spreadsheetId, CancellationToken ct = default)
        {
            Gets++;
            return Task.FromResult(store.TryGetValue(spreadsheetId, out var tabs) ? tabs : null);
        }

        public Task SetAsync(string spreadsheetId, IReadOnlyList<SheetTab> tabs, CancellationToken ct = default)
        {
            Sets++;
            store[spreadsheetId] = tabs;
            return Task.CompletedTask;
        }

        public Task InvalidateAsync(string spreadsheetId, CancellationToken ct = default)
        {
            Invalidates++;
            store.Remove(spreadsheetId);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingCache : ISheetTabCache
    {
        public Task<IReadOnlyList<SheetTab>?> GetAsync(string spreadsheetId, CancellationToken ct = default) => throw new InvalidOperationException("redis down");
        public Task SetAsync(string spreadsheetId, IReadOnlyList<SheetTab> tabs, CancellationToken ct = default) => throw new InvalidOperationException("redis down");
        public Task InvalidateAsync(string spreadsheetId, CancellationToken ct = default) => throw new InvalidOperationException("redis down");
    }

    private sealed class RemoteClient : ISheetClient
    {
        public int Reads, Writes;
        public Task<IReadOnlyList<SheetTab>> GetTabsAsync(string spreadsheetId, IEnumerable<string>? tabNames = null, CancellationToken ct = default)
        {
            Reads++;
            IReadOnlyList<SheetTab> tabs = tabNames is null ? Tabs : Tabs.Where(t => tabNames.Contains(t.Name)).ToList();
            return Task.FromResult(tabs);
        }
        public Task WriteColumnAsync(string id, string tab, int index, SheetColumn column, CancellationToken ct = default) { Writes++; return Task.CompletedTask; }
        public Task InsertColumnAsync(string id, string tab, int index, SheetColumn column, CancellationToken ct = default) { Writes++; return Task.CompletedTask; }
        public Task DeleteColumnAsync(string id, string tab, int index, CancellationToken ct = default) { Writes++; return Task.CompletedTask; }
        public Task ApplyManagedMutationAsync(string id, ManagedSheetTab expected, ManagedSheetMutation mutation, CancellationToken ct = default) { Writes++; return Task.CompletedTask; }
        public Task RestoreManagedTabAsync(string id, ManagedSheetTab before, ManagedSheetTab expected, ManagedSheetMutation mutation, CancellationToken ct = default) { Writes++; return Task.CompletedTask; }
    }

    private static CachedSheetClient Create(RemoteClient remote, ISheetTabCache cache) =>
        new(remote, cache, NullLogger<CachedSheetClient>.Instance);

    [Fact]
    public async Task 첫_전체_조회는_원본을_읽고_캐시에_저장한다()
    {
        var remote = new RemoteClient();
        var cache = new FakeCache();
        var client = Create(remote, cache);

        var result = await client.GetTabsAsync("sheet-1");

        Assert.Equal(Tabs, result);
        Assert.Equal(1, remote.Reads);
        Assert.Equal(1, cache.Sets);
    }

    [Fact]
    public async Task 두번째_전체_조회는_캐시로_원본을_우회한다()
    {
        var remote = new RemoteClient();
        var client = Create(remote, new FakeCache());
        await client.GetTabsAsync("sheet-1");

        var result = await client.GetTabsAsync("sheet-1");

        Assert.Equal(Tabs, result);
        Assert.Equal(1, remote.Reads); // 원본 읽기는 첫 조회 1회뿐
    }

    [Fact]
    public async Task 부분_탭_조회는_캐시를_우회한다()
    {
        var remote = new RemoteClient();
        var cache = new FakeCache();
        var client = Create(remote, cache);

        await client.GetTabsAsync("sheet-1");
        await client.GetTabsAsync("sheet-1", ["2026-2"]);

        Assert.Equal(2, remote.Reads);
        Assert.Equal(1, cache.Gets); // 부분 조회는 캐시를 아예 묻지 않는다
    }

    [Fact]
    public async Task 열_쓰기_성공_후_해당_캐시가_즉시_무효화된다()
    {
        var cache = new FakeCache();
        var remote = new RemoteClient();
        var client = Create(remote, cache);
        await client.GetTabsAsync("sheet-1");

        await client.WriteColumnAsync("sheet-1", "2026-2", 3, new SheetColumn(2, ["1"]));
        await client.InsertColumnAsync("sheet-1", "2026-2", 3, new SheetColumn(2, ["1"]));
        await client.DeleteColumnAsync("sheet-1", "2026-2", 3);

        Assert.Equal(3, cache.Invalidates);
        Assert.Equal(1, remote.Reads); // 재조회 전이라 원본은 이전 1회
        await client.GetTabsAsync("sheet-1");
        Assert.Equal(2, remote.Reads); // 무효화 뒤 재조회는 원본에서 다시 읽는다
    }

    [Fact]
    public async Task 관리형_변경_적용과_복원_후_캐시가_무효화된다()
    {
        var cache = new FakeCache();
        var remote = new RemoteClient();
        var client = Create(remote, cache);
        await client.GetTabsAsync("sheet-1");
        var tab = new ManagedSheetTab("2026-2", [], [], new Dictionary<int, string>());
        var mutation = new ManagedSheetMutation("op-1", SheetMutationKind.Write, 3, "src", []);

        await client.ApplyManagedMutationAsync("sheet-1", tab, mutation);
        await client.RestoreManagedTabAsync("sheet-1", tab, tab, mutation);

        Assert.Equal(2, cache.Invalidates);
        Assert.Equal(2, remote.Writes);
    }

    [Fact]
    public async Task 캐시_저장소_오류는_읽기와_쓰기를_해치지_않는다()
    {
        var remote = new RemoteClient();
        var client = Create(remote, new ThrowingCache());

        var result = await client.GetTabsAsync("sheet-1");
        await client.WriteColumnAsync("sheet-1", "2026-2", 3, new SheetColumn(2, ["1"]));

        Assert.Equal(Tabs, result);   // 캐시 조회 실패 → 원본 결과 반환
        Assert.Equal(1, remote.Reads);
        Assert.Equal(1, remote.Writes); // 쓰기는 성공(무효화 실패는 경고만)
    }
}
