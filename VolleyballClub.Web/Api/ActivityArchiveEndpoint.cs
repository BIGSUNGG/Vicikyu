using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using VolleyballClub.Application.Abstractions;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Application.Exceptions;
using VolleyballClub.Infrastructure.Services;

namespace VolleyballClub.Web.Api;

/// <summary>활동 기록 xlsx 업로드(스트리밍 파싱 — 서버 디스크에 파일을 저장하지 않는다)·내보내기.</summary>
internal static class ActivityArchiveEndpoint
{
    internal static async Task<IResult> Import(HttpRequest request, IActivityArchiveService archive)
    {
        if (!request.HasFormContentType || request.ContentLength > 2 * ActivityWorkbookReader.MaxFileBytes)
            throw new RuleViolationException("8MB 이하의 XLSX 파일을 선택해 주세요.");
        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync(new FormOptions
            {
                MultipartBodyLengthLimit = ActivityWorkbookReader.MaxFileBytes,
                ValueLengthLimit = 4096, ValueCountLimit = 3, KeyLengthLimit = 64,
                MultipartHeadersCountLimit = 8, MultipartHeadersLengthLimit = 2048,
                MultipartBoundaryLengthLimit = 128
            });
        }
        catch (InvalidDataException) { throw new RuleViolationException("업로드 형식 또는 크기 제한을 확인해 주세요."); }
        if (form.Files.Count != 1 || form.Files[0].Length > ActivityWorkbookReader.MaxFileBytes ||
            !Path.GetExtension(form.Files[0].FileName).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new RuleViolationException("8MB 이하의 XLSX 파일 하나를 선택해 주세요.");
        using var buffer = new MemoryStream();
        await form.Files[0].CopyToAsync(buffer);
        buffer.Position = 0;
        var report = await archive.ImportAsync(buffer);
        return Results.Json(report);
    }

    internal static async Task<IResult> Export(IActivityArchiveService archive, TimeProvider clock)
    {
        var bytes = await archive.ExportAsync();
        var now = VolleyballClub.Domain.KstClock.NowUtc(clock);
        return Results.File(bytes,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"활동기록-{now:yyyyMMdd-HHmm}.xlsx");
    }
}
