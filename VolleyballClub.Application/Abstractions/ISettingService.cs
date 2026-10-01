namespace VolleyballClub.Application.Abstractions;

public interface ISettingService
{
    Task<string?> GetAsync(string key);

    Task<int> GetIntAsync(string key, int fallback);

    Task SetAsync(string key, string value);

    Task<Dictionary<string, string>> GetAllAsync();

    Task<string> GetClubDisplayNameAsync();

    Task<string?> GetFeeSheetUrlAsync();
}
