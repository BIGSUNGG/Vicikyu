using System.Net;
using System.Net.Http.Json;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Application.Exceptions;

namespace VolleyballClub.Client.Services;

public sealed class ApiHttp(HttpClient http)
{
    private string? token;
    private readonly SemaphoreSlim tokenLock = new(1, 1);
    public event Action? Unauthorized;
    public event Func<Task>? IdentityChanged;

    public async Task RefreshIdentityAsync()
    {
        token = null;
        if (IdentityChanged is { } changed) await changed();
        await GetTokenAsync();
    }

    private async Task<string> GetTokenAsync()
    {
        await tokenLock.WaitAsync();
        try
        {
            return token ??= (await http.GetFromJsonAsync<CsrfTokenDto>("api/auth/csrf"))!.Token;
        }
        finally { tokenLock.Release(); }
    }

    public async Task<T?> GetNullableAsync<T>(string path) where T : class => await SendAsync<T?>(HttpMethod.Get, path);
    public async Task<T> GetAsync<T>(string path) => await SendAsync<T>(HttpMethod.Get, path);
    public async Task<T> PostAsync<T>(string path, object? body = null) => await SendAsync<T>(HttpMethod.Post, path, body);
    public async Task PostAsync(string path, object? body = null) => await SendAsync<object?>(HttpMethod.Post, path, body);
    public async Task<T> PutAsync<T>(string path, object body) => await SendAsync<T>(HttpMethod.Put, path, body);
    public async Task PutAsync(string path, object body) => await SendAsync<object?>(HttpMethod.Put, path, body);
    public async Task DeleteAsync(string path, object? body = null) => await SendAsync<object?>(HttpMethod.Delete, path, body);

    /// <summary>multipart 폼 업로드(파일 첨부) — CSRF 토큰 처리는 다른 메서드와 동일.</summary>
    public async Task<T> PostFormAsync<T>(string path, HttpContent form)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = form };
        request.Headers.Add("X-CSRF-TOKEN", await GetTokenAsync());
        using var response = await http.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            token = null;
            Unauthorized?.Invoke();
            throw new ForbiddenException("로그인이 필요합니다.");
        }
        if (!response.IsSuccessStatusCode)
        {
            var error = response.Content.Headers.ContentType?.MediaType == "application/json"
                ? await response.Content.ReadFromJsonAsync<ApiErrorDto>() : null;
            throw new AppException(error?.Message ?? "요청을 처리하지 못했습니다.");
        }
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = body as HttpContent ?? JsonContent.Create(body, body.GetType());
        if (method != HttpMethod.Get) request.Headers.Add("X-CSRF-TOKEN", await GetTokenAsync());
        using var response = await http.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            token = null;
            Unauthorized?.Invoke();
            throw new ForbiddenException("로그인이 필요합니다.");
        }
        if (!response.IsSuccessStatusCode)
        {
            var error = response.Content.Headers.ContentType?.MediaType == "application/json"
                ? await response.Content.ReadFromJsonAsync<ApiErrorDto>() : null;
            var message = error?.Message ?? "요청을 처리하지 못했습니다.";
            if (error?.CorrelationId is { } id) message += $" (요청 ID: {id})";
            throw response.StatusCode switch
            {
                HttpStatusCode.Forbidden => new ForbiddenException(message),
                HttpStatusCode.NotFound => new NotFoundException(message),
                HttpStatusCode.Conflict => new ConflictException(message),
                _ => new AppException(message)
            };
        }
        if (response.StatusCode == HttpStatusCode.NoContent) return default!;
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    /// <summary>인증이 필요한 파일 다운로드 — 바이트로 받는다(저장 대화상자는 호출처에서 JS로 띄운다).</summary>
    public async Task<byte[]> GetBytesAsync(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        using var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var error = response.Content.Headers.ContentType?.MediaType == "application/json"
                ? await response.Content.ReadFromJsonAsync<ApiErrorDto>() : null;
            throw new AppException(error?.Message ?? "파일을 내려받지 못했습니다.");
        }
        return await response.Content.ReadAsByteArrayAsync();
    }

    public static string Escape(string? value) => Uri.EscapeDataString(value ?? "");
}
