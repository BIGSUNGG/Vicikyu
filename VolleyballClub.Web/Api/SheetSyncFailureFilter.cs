using VolleyballClub.Application.Abstractions;

namespace VolleyballClub.Web.Api;

internal sealed class SheetSyncFailureFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var result = await next(context);
        var sync = context.HttpContext.RequestServices.GetRequiredService<ISheetSyncService>();
        if (sync.LastPushResult is { Success: false })
            context.HttpContext.Response.Headers["X-Sheet-Sync-Failed"] = "true";
        return result;
    }
}
