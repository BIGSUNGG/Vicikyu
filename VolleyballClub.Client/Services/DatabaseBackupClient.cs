namespace VolleyballClub.Client.Services;

public sealed class DatabaseBackupClient(ApiHttp api)
{
    public Task RestoreAsync(Stream file, string name)
    {
        var form = new MultipartFormDataContent();
        form.Add(new StreamContent(file), "file", name);
        form.Add(new StringContent("RESTORE"), "confirm");
        return api.PostAsync("api/admin/database/import", form);
    }
}
