using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using VolleyballClub.Application.Abstractions;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Application.Exceptions;

namespace VolleyballClub.Web.Api;

internal static class MemberImportEndpoint
{
    internal static async Task<IResult> Preview(HttpRequest request, IMemberWorkbookReader reader)
    {
        if (!request.HasFormContentType || request.ContentLength > 2 * MemberBulkValidation.MaxFileBytes)
            throw new RuleViolationException("1MB 이하의 XLSX 파일을 선택해 주세요.");
        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync(new FormOptions
            {
                MultipartBodyLengthLimit = MemberBulkValidation.MaxFileBytes,
                ValueLengthLimit = 4096, ValueCountLimit = 3, KeyLengthLimit = 64,
                MultipartHeadersCountLimit = 8, MultipartHeadersLengthLimit = 2048,
                MultipartBoundaryLengthLimit = 128
            });
        }
        catch (InvalidDataException) { throw new RuleViolationException("업로드 형식 또는 크기 제한을 확인해 주세요."); }
        var role = form["role"].ToString();
        var password = form["password"].ToString();
        if (!MemberBulkValidation.ValidDefaults(role, password))
            throw new RuleViolationException("기본 역할과 4자 이상의 초기 비밀번호를 먼저 설정해 주세요.");
        if (form.Files.Count != 1 || form.Files[0].Length > MemberBulkValidation.MaxFileBytes ||
            !Path.GetExtension(form.Files[0].FileName).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new RuleViolationException("1MB 이하의 XLSX 파일 하나를 선택해 주세요.");
        using var buffer = new MemoryStream();
        await form.Files[0].CopyToAsync(buffer);
        buffer.Position = 0;
        return Results.Json(reader.Read(buffer, role, password));
    }
}
