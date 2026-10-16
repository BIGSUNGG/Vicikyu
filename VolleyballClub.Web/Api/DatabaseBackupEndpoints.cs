using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using VolleyballClub.Application.Abstractions;
using VolleyballClub.Application.Exceptions;

namespace VolleyballClub.Web.Api;

internal sealed class DatabaseRestoreMetadata { }

internal static class DatabaseBackupEndpoints
{
    internal static void MapDatabaseBackupApi(this WebApplication app)
    {
        var database = app.MapGroup("/api/admin/database").RequireAuthorization(p => p.RequireRole("ADMIN"));
        database.MapGet("/export", async (IDatabaseBackupService service, HttpContext context) =>
            Results.Stream(await service.ExportAsync(context.RequestAborted), "application/octet-stream", $"vicikyu-{DateTime.UtcNow:yyyyMMdd-HHmmss}.db"));
        database.MapPost("/import", async (HttpContext context, IDatabaseBackupService service) =>
        {
            var request = context.Request;
            if (!request.HasFormContentType || request.ContentLength > DatabaseBackupLimits.MaxFileBytes + 65536)
                throw new RuleViolationException("64MB 이하의 .db 파일 하나를 선택해 주세요.");
            IFormCollection form;
            try
            {
                form = await request.ReadFormAsync(new FormOptions
                {
                    MultipartBodyLengthLimit = DatabaseBackupLimits.MaxFileBytes,
                    // ponytail: one exclusive admin upload buffers at most 64MB; stream multipart if this limit grows.
                    // Keep secret-bearing uploads out of ASP.NET's shared OS temporary-file directory.
                    MemoryBufferThreshold = checked((int)DatabaseBackupLimits.MaxFileBytes),
                    ValueLengthLimit = 128, ValueCountLimit = 2, KeyLengthLimit = 64,
                    MultipartHeadersCountLimit = 8, MultipartHeadersLengthLimit = 2048,
                    MultipartBoundaryLengthLimit = 128
                }, context.RequestAborted);
            }
            catch (InvalidDataException) { throw new RuleViolationException("업로드 형식 또는 64MB 크기 제한을 확인해 주세요."); }
            if (form["confirm"] != "RESTORE" || form.Files.Count != 1 || form.Files[0].Length == 0 ||
                form.Files[0].Length > DatabaseBackupLimits.MaxFileBytes ||
                !Path.GetExtension(form.Files[0].FileName).Equals(".db", StringComparison.OrdinalIgnoreCase))
                throw new RuleViolationException("전체 교체를 확인하고 64MB 이하의 .db 파일 하나를 선택해 주세요.");
            await using var stream = form.Files[0].OpenReadStream();
            await service.RestoreAsync(stream, context.RequestAborted);
            await context.SignOutAsync(IdentityConstants.ApplicationScheme);
            return Results.NoContent();
        }).WithMetadata(new DatabaseRestoreMetadata(), new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(DatabaseBackupLimits.MaxFileBytes + 65536));
    }
}
