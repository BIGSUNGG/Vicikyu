using System.Text.Json;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using VolleyballClub.Application.Dtos;

namespace VolleyballClub.Infrastructure.Services;

/// <summary>Redis 기반 ISheetTabCache. 값은 JSON으로 직렬화해 저장한다.
/// TTL은 시트를 시트 UI 등 앱 밖에서 직접 편집했을 때의 최대 반영 지연 상한(기본 5분)이며,
/// 앱 내 쓰기는 CachedSheetClient가 즉시 무효화한다. 캐시 특성상 재시작 시 유지할 필요가 없어
/// 볼륨을 붙이지 않는다.</summary>
public sealed class RedisSheetTabCache(IConnectionMultiplexer redis, ILogger<RedisSheetTabCache> logger) : ISheetTabCache
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General);
    private const string KeyPrefix = "sheet:tabs:";

    /// <summary>외부 직접 편집 반영 지연 상한.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    public async Task<IReadOnlyList<SheetTab>?> GetAsync(string spreadsheetId, CancellationToken ct = default)
    {
        var value = await redis.GetDatabase().StringGetAsync(KeyPrefix + spreadsheetId);
        if (value.IsNull) return null;
        try
        {
            return JsonSerializer.Deserialize<IReadOnlyList<SheetTab>>((string)value!, Json);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "시트 탭 캐시 값 역직렬화 실패 — 무효화 후 원본 조회로 대체: {SpreadsheetId}", spreadsheetId);
            await InvalidateAsync(spreadsheetId, ct);
            return null;
        }
    }

    public Task SetAsync(string spreadsheetId, IReadOnlyList<SheetTab> tabs, CancellationToken ct = default) =>
        redis.GetDatabase().StringSetAsync(
            KeyPrefix + spreadsheetId,
            JsonSerializer.Serialize(tabs, Json),
            Ttl);

    public Task InvalidateAsync(string spreadsheetId, CancellationToken ct = default) =>
        redis.GetDatabase().KeyDeleteAsync(KeyPrefix + spreadsheetId);
}
