using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Application.Exceptions;

namespace VolleyballClub.Client.Services;

/// <summary>활동 기록 xlsx 가져오기·내보내기 + 미매핑 부원 관리(관리자 설정 페이지).</summary>
public sealed class ActivityArchiveClient(ApiHttp api, IJSRuntime js)
{
    public async Task<ActivityImportReport> ImportAsync(IBrowserFile file)
    {
        await using var stream = file.OpenReadStream(ActivityWorkbookLimits.MaxFileBytes);
        using var form = new MultipartFormDataContent
        {
            { new StreamContent(stream), "file", file.Name },
        };
        return await api.PostFormAsync<ActivityImportReport>("api/admin/activities/import", form);
    }

    public async Task ExportAsync()
    {
        var bytes = await api.GetBytesAsync("api/admin/activities/export");
        var module = await js.InvokeAsync<IJSObjectReference>("import", "./js/file-save.js");
        try { await module.InvokeVoidAsync("saveAs", "활동기록.xlsx", bytes); }
        finally { await module.DisposeAsync(); }
    }

    public Task<List<UnmappedMemberDto>> GetUnmappedAsync() => api.GetAsync<List<UnmappedMemberDto>>("api/admin/unmapped-members");

    public Task LinkAsync(int unmappedMemberId, string userId) =>
        api.PostAsync($"api/admin/unmapped-members/{unmappedMemberId}/link", new TextRequest(userId));
}

/// <summary>업로드 크기 상한 — 서버(ActivityWorkbookReader.MaxFileBytes)와 동일.</summary>
public static class ActivityWorkbookLimits
{
    public const long MaxFileBytes = 8 * 1024 * 1024;
}
