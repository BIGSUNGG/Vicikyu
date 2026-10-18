using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Application.Exceptions;

namespace VolleyballClub.Infrastructure.Services;

// The same deterministic postimage is used before journaling, for delivery verification and before inverse recovery.
internal static class MemberRowEdits
{
    internal static void Validate(ManagedSheetTab before, ManagedSheetMutation mutation)
    {
        var (start, end) = SemesterTabFormat.MemberRegion(new(before.Name, before.Rows));
        if (end >= before.Rows.Count || end <= start || mutation.RowIndex != end + 1
            || before.Rows[end - 1].Count == 0 || string.IsNullOrWhiteSpace(before.Rows[end - 1][0])
            || mutation.RawValues.Count != 1
            || JsonNode.Parse(mutation.RawValues[0]) is not JsonObject label || label.Count != 1
            || label["stringValue"] is null)
            throw new ConflictException("부원 구간/집계 경계를 안전하게 확인할 수 없습니다. 기존 부원과 총 참여자 수 또는 탈퇴/퇴출자 경계를 확인하세요.");
        for (var r = 0; r < before.RawValues.Count; r++)
            for (var c = 0; c < before.RawValues[r].Count; c++)
                if (JsonNode.Parse(before.RawValues[r][c])?["formulaValue"]?.GetValue<string>() is { } formula)
                {
                    if (r != end || before.Rows[end][0].Trim() != "총 참여자 수") Unsupported();
                    ExpandTotal(formula, c + 1, start + 1, end);
                }
    }

    internal static string ExpandTotal(string formula, int column, int first, int last)
    {
        var name = ManagedSheetEdits.ColumnLetter(column);
        var pattern = $"^(=SUM\\(\\$?{name}\\$?{first}:\\$?{name}\\$?){last}(\\))$|^(=COUNTIF\\(\\$?{name}\\$?{first}:\\$?{name}\\$?){last}(,\"출석\"\\))$";
        var match = Regex.Match(formula, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) Unsupported();
        return match.Groups[1].Success ? match.Groups[1].Value + (last + 1) + match.Groups[2].Value
            : match.Groups[3].Value + (last + 1) + match.Groups[4].Value;
    }

    private static void Unsupported() => throw new ConflictException("부원 전체 범위의 SUM/COUNTIF 집계 외 수식은 안전한 행 추가를 지원하지 않습니다. 원본 서식을 확인하세요.");

    internal static void Transform(List<List<string>> grid, ManagedSheetMutation mutation, bool cells)
    {
        var index = mutation.RowIndex - 1;
        // The preimage already passed Validate. Only the boundary totals change; every other existing cell stays raw.
        var start = SemesterTabFormat.MemberRegion(new("", grid.Select(r => (IReadOnlyList<string>)r.Select(raw =>
        {
            var node = JsonNode.Parse(raw)!;
            return cells ? node["userEnteredValue"]?["stringValue"]?.GetValue<string>() ?? ""
                : node["stringValue"]?.GetValue<string>() ?? "";
        }).ToList()).ToList())).Start;
        foreach (var row in grid)
            for (var c = 0; c < row.Count; c++)
            {
                var node = JsonNode.Parse(row[c])!.AsObject();
                var value = cells ? node["userEnteredValue"] : node;
                if (value?["formulaValue"]?.GetValue<string>() is { } formula)
                {
                    value["formulaValue"] = ExpandTotal(formula, c + 1, start + 1, index);
                    row[c] = node.ToJsonString();
                }
            }
        var inserted = new List<string>();
        if (cells)
            foreach (var raw in grid[index - 1])
            {
                var prior = JsonNode.Parse(raw)!;
                var cell = new JsonObject();
                foreach (var property in new[] { "userEnteredFormat", "dataValidation" })
                    if (prior[property] is { } part) cell[property] = part.DeepClone();
                inserted.Add(cell.ToJsonString());
            }
        if (inserted.Count == 0) inserted.Add("{}");
        if (cells)
        {
            var label = JsonNode.Parse(inserted[0])!.AsObject();
            label["userEnteredValue"] = JsonNode.Parse(mutation.RawValues[0]);
            inserted[0] = label.ToJsonString();
        }
        else inserted[0] = mutation.RawValues[0];
        grid.Insert(index, inserted);
    }
}
