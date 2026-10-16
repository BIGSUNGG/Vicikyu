using VolleyballClub.Application.Abstractions;
using VolleyballClub.Application.Dtos;
using Microsoft.AspNetCore.Http.Features;

namespace VolleyballClub.Web.Api;

/// <summary>Gate before authentication, including public shell/static requests carrying a cookie.</summary>
public sealed class DatabaseMaintenanceMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IDatabaseMaintenance maintenance, IServiceScopeFactory scopes)
    {
        // Endpoint metadata covers route aliases (case and trailing slash), not a fragile path comparison.
        var restore = context.GetEndpoint()?.Metadata.GetMetadata<DatabaseRestoreMetadata>() is not null;
        // Header-only CSRF prevents unauthenticated slow multipart bodies from holding the exclusive lease.
        if (restore && string.IsNullOrWhiteSpace(context.Request.Headers["X-CSRF-TOKEN"]))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsJsonAsync(new ApiErrorDto("csrf", "요청 인증 토큰이 올바르지 않습니다. 새로고침 후 다시 시도해 주세요."));
            return;
        }
        using var lease = await maintenance.EnterAsync(restore, context.RequestAborted);
        if (maintenance.IsBlocked)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsJsonAsync(new ApiErrorDto("maintenance", "DB 복구 확인이 필요하여 서비스를 중단했습니다. 운영자에게 문의해 주세요."));
            return;
        }
        if (restore && context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = DatabaseBackupLimits.MaxFileBytes + 65536;

        // Dispose tracked Identity/business contexts BEFORE releasing the lease to a restore.
        var originalServices = context.RequestServices;
        await using var scope = scopes.CreateAsyncScope();
        context.RequestServices = scope.ServiceProvider;
        try { await next(context); }
        finally { context.RequestServices = originalServices; }
    }
}
