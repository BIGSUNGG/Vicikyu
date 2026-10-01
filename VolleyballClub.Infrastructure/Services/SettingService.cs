using Microsoft.EntityFrameworkCore;
using VolleyballClub.Application.Abstractions;
using VolleyballClub.Domain.Entities;
using VolleyballClub.Infrastructure.Persistence;

namespace VolleyballClub.Infrastructure.Services;

public class SettingService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ICurrentUserService currentUser,
    TimeProvider timeProvider) : ISettingService
{
    public async Task<string?> GetAsync(string key)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Settings.AsNoTracking()
            .Where(s => s.Key == key)
            .Select(s => s.Value)
            .FirstOrDefaultAsync();
    }

    public async Task<int> GetIntAsync(string key, int fallback)
    {
        var raw = await GetAsync(key);
        return int.TryParse(raw, out var value) ? value : fallback;
    }

    public async Task SetAsync(string key, string value)
    {
        await currentUser.RequireAdminAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var setting = await db.Settings.FirstOrDefaultAsync(s => s.Key == key);
        if (setting is null)
        {
            db.Settings.Add(new Setting { Key = key, Value = value, UpdatedAt = timeProvider.GetUtcNow().UtcDateTime });
        }
        else
        {
            setting.Value = value;
            setting.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        }

        await db.SaveChangesAsync();
    }

    public async Task<Dictionary<string, string>> GetAllAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Settings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value);
    }

    public async Task<string> GetClubDisplayNameAsync()
    {
        var club = await GetAsync("ClubName") ?? "배구부";
        var school = await GetAsync("SchoolName") ?? "";
        return string.IsNullOrWhiteSpace(school) ? club : $"🏐 {school} {club}";
    }

    public async Task<string?> GetFeeSheetUrlAsync() => await GetAsync("FeeSheetUrl");
}
