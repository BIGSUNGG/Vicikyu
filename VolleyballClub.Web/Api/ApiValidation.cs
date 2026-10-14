using VolleyballClub.Application.Abstractions;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Application.Exceptions;

namespace VolleyballClub.Web.Api;

/// <summary>HTTP trust-boundary validation; business rules remain in the services.</summary>
public sealed class ApiValidation : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        foreach (var value in context.Arguments)
        {
            switch (value)
            {
                case MemberCreateRequest r:
                    Required(r.StudentNumber, r.Name, r.Password); NonNull(r.Department);
                    Role(r.Role); break;
                case MemberUpdateRequest r:
                    Required(r.Name); NonNull(r.Department); if (r.Role is not null) Role(r.Role);
                    if (r.Status is { } status && !Enum.IsDefined(status)) Invalid(); break;
                case PasswordChangeRequest r: Required(r.CurrentPassword, r.NewPassword); break;
                case TextRequest r: Required(r.Value); break;
                case SheetSaveRequest r: Required(r.Name, r.SpreadsheetIdOrUrl); break;
                case SheetMappingRequest r: Required(r.TabName); if (r.SheetId <= 0) Invalid(); break;
                case TeamCreateRequest r:
                    if (r.UserIds is null || r.GuestIds is null || r.TeamSizes is null || r.UserIds.Any(string.IsNullOrWhiteSpace)) Invalid(); break;
                case TeamAddRequest r: Required(r.ParticipantKey); if (r.TeamId <= 0) Invalid(); break;
                case TeamMoveRequest r: if (r.TargetTeamId <= 0) Invalid(); break;
                case FeePeriodCreateRequest r: Required(r.Name); break;
                case FeePeriodUpdateRequest r: Required(r.Name); break;
                case SemesterSaveRequest r: Required(r.Name); break;
                case MemberPageRequest r:
                    if (!Enum.IsDefined(r.Sort) || r.Statuses?.Any(s => !Enum.IsDefined(s)) == true || r.Page < 1 || r.PageSize < 1 || r.PageSize > 100) Invalid(); break;
                case FeeFilter filter: if (!Enum.IsDefined(filter)) Invalid(); break;
            }
        }
        var query = context.HttpContext.Request.Query;
        if (query.ContainsKey("year") && (!int.TryParse(query["year"], out var year) || year is < 1 or > 9999)) Invalid();
        if (query.ContainsKey("month") && (!int.TryParse(query["month"], out var month) || month is < 1 or > 12)) Invalid();
        if (query.ContainsKey("take") && (!int.TryParse(query["take"], out var take) || take is < 1 or > 1000)) Invalid();
        return await next(context);
    }

    private static void Required(params string?[] values) { if (values.Any(string.IsNullOrWhiteSpace)) Invalid(); }
    private static void NonNull(string? value) { if (value is null) Invalid(); }
    private static void Role(string? role) { if (role is not ("ADMIN" or "MEMBER")) Invalid(); }
    private static void Invalid() => throw new RuleViolationException("요청 값이 올바르지 않습니다.");
}
