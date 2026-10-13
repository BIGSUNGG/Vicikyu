namespace VolleyballClub.Application.Abstractions;

public interface ISettingService
{
    Task SetAsync(string key, string value);
}
