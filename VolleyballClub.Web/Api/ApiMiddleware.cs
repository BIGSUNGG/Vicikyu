using Microsoft.AspNetCore.Antiforgery;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Application.Exceptions;

namespace VolleyballClub.Web.Api;

public sealed class ApiMiddleware(RequestDelegate next, ILogger<ApiMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, IAntiforgery antiforgery)
    {
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await next(context);
            return;
        }
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) && !HttpMethods.IsOptions(context.Request.Method))
                await antiforgery.ValidateRequestAsync(context);
            await next(context);
        }
        catch (Exception ex) when (!context.Response.HasStarted)
        {
            var (status, kind, message) = ex switch
            {
                AntiforgeryValidationException => (400, "csrf", "요청 인증 토큰이 올바르지 않습니다. 새로고침 후 다시 시도해 주세요."),
                BadHttpRequestException => (400, "validation", "요청 형식이 올바르지 않습니다."),
                NotFoundException => (404, "not-found", ex.Message),
                ForbiddenException => (403, "forbidden", ex.Message),
                ConflictException => (409, "conflict", ex.Message),
                AppException => (400, "rule", ex.Message),
                _ => (500, "unexpected", "처리 중 오류가 발생했습니다. 다시 시도해 주세요.")
            };
            if (status == 500) logger.LogError(ex, "API failure {CorrelationId}", context.TraceIdentifier);
            context.Response.StatusCode = status;
            await context.Response.WriteAsJsonAsync(new ApiErrorDto(kind, message, status == 500 ? context.TraceIdentifier : null));
        }
    }
}
