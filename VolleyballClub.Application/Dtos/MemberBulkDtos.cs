namespace VolleyballClub.Application.Dtos;

public record MemberBulkRequest(IReadOnlyList<MemberCreateRequest> Rows);
public record MemberRowError(int Row, string Message);
public record MemberBulkResult(IReadOnlyList<MemberListDto> Created, IReadOnlyList<MemberRowError> Errors);

/// <summary>Shared preview rules; server also checks Identity and database constraints.</summary>
public static class MemberBulkValidation
{
    public const int MaxRows = 500;
    public const int MaxFileBytes = 1024 * 1024;
    public static bool ValidDefaults(string? role, string? password) =>
        role is "ADMIN" or "MEMBER" && !string.IsNullOrWhiteSpace(password) && password is { Length: >= 4 and <= 4096 };

    public static List<MemberRowError> Validate(IReadOnlyList<MemberCreateRequest>? rows)
    {
        List<MemberRowError> errors = [];
        if (rows is null || rows.Count == 0 || rows.Count > MaxRows)
        {
            errors.Add(new(0, $"1~{MaxRows}개 행을 등록해 주세요."));
            return errors;
        }
        var duplicates = rows.Where(r => r is not null && !string.IsNullOrWhiteSpace(r.StudentNumber))
            .GroupBy(r => r.StudentNumber.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            void Error(string message) => errors.Add(new(i + 1, message));
            if (r is null) { Error("행 값이 올바르지 않습니다."); continue; }
            if (string.IsNullOrWhiteSpace(r.StudentNumber) || string.IsNullOrWhiteSpace(r.Name)) Error("학번과 이름은 필수입니다.");
            if (r.Department is null) Error("학과 값이 올바르지 않습니다.");
            if (string.IsNullOrWhiteSpace(r.Password) || r.Password.Length < 4) Error("비밀번호는 공백만으로 구성할 수 없으며 4자 이상이어야 합니다.");
            if (r.Role is not ("ADMIN" or "MEMBER")) Error("역할은 ADMIN 또는 MEMBER여야 합니다.");
            if (new[] { r.StudentNumber, r.Name, r.Department, r.Password }.Any(v => v?.Length > 4096)) Error("필드 값은 4096자 이하여야 합니다.");
            if (r.StudentNumber is not null && duplicates.Contains(r.StudentNumber.Trim())) Error("목록 안에 중복 학번이 있습니다.");
        }
        return errors;
    }
}
