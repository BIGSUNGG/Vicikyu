namespace VolleyballClub.Client.Services;

public sealed class DatabaseBackupClient(ApiHttp api)
{
    private sealed record RetentionResponse(int Days);

    public Task RestoreAsync(Stream file, string name)
    {
        var form = new MultipartFormDataContent();
        form.Add(new StreamContent(file), "file", name);
        form.Add(new StringContent("RESTORE"), "confirm");
        return api.PostAsync("api/admin/database/import", form);
    }

    public async Task<int> GetRetentionDaysAsync() =>
        (await api.GetAsync<RetentionResponse>("api/admin/database/backup-retention")).Days;

    public Task SetRetentionDaysAsync(int days) => api.PutAsync("api/admin/database/backup-retention", new { days });
}
