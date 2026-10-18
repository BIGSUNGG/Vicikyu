using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using VolleyballClub.Application.Abstractions;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Application.Exceptions;
using VolleyballClub.Domain.Entities;
using VolleyballClub.Infrastructure.Persistence;

namespace VolleyballClub.Infrastructure.Services;

/// <summary>One process-wide boundary shared by imports, exports and managed edits.
/// A durable journal blocks further sync if recovery cannot be verified.</summary>
internal static class ManagedSheetEdits
{
    internal static readonly SemaphoreSlim Gate = new(1, 1);
    // Remote I/O never holds this gate: OTP may commit while a managed edit is in flight.
    internal static readonly SemaphoreSlim AttendanceCommitGate = new(1, 1);
    internal static string Json<T>(T value) => JsonSerializer.Serialize(value);
    internal static string Raw(string value) => value.Length == 0 ? "{}"
        : value == "1" ? "{\"numberValue\":1}"
        : Json(new Dictionary<string, object> { [value.StartsWith('=') ? "formulaValue" : "stringValue"] = value });
    internal static string Text(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.EnumerateObject().FirstOrDefault().Value.ToString();
    }

    internal static void RequireOwnedPostimage(ManagedSheetTab before, ManagedSheetTab actual, ManagedSheetMutation mutation)
    {
        if (mutation.Kind == SheetMutationKind.InsertMemberRow)
        {
            var rows = before.RawValues.Select(r => r.ToList()).ToList();
            MemberRowEdits.Transform(rows, mutation, false);
            if (actual.LastMutation != mutation.OperationId
                || Json(before.ColumnKeys.OrderBy(k => k.Key)) != Json(actual.ColumnKeys.OrderBy(k => k.Key))
                || !GridEquals(rows, actual.RawValues)) Diverged();
            if (before.RawCellData is not null)
            {
                var cells = before.RawCellData.Select(r => r.ToList()).ToList();
                MemberRowEdits.Transform(cells, mutation, true);
                if (actual.RawCellData is null || !GridEquals(cells, actual.RawCellData)) Diverged();
            }
            if (Json(before.OtherColumnMetadata) != Json(actual.OtherColumnMetadata)) Diverged();
            return;
        }
        var values = before.RawValues.Select(r => r.ToList()).ToList();
        var keys = new Dictionary<int, string>();
        foreach (var key in before.ColumnKeys)
        {
            if (mutation.Kind == SheetMutationKind.Delete && key.Key == mutation.ColumnIndex) continue;
            keys[key.Key + (mutation.Kind == SheetMutationKind.Insert && key.Key >= mutation.ColumnIndex ? 1
                : mutation.Kind == SheetMutationKind.Delete && key.Key > mutation.ColumnIndex ? -1 : 0)] = key.Value;
        }
        if (mutation.Kind is SheetMutationKind.Insert or SheetMutationKind.Bind) keys[mutation.ColumnIndex] = mutation.SourceKey;
        Transform(values, mutation, "{}");
        if (actual.LastMutation != mutation.OperationId) Diverged("marker");
        if (Json(keys.OrderBy(k => k.Key)) != Json(actual.ColumnKeys.OrderBy(k => k.Key))) Diverged("keys");
        if (!GridEquals(values, actual.RawValues))
        {
            for (var r = 0; r < Math.Max(values.Count, actual.RawValues.Count); r++)
            {
                var b = r < values.Count ? values[r] : [];
                var a = r < actual.RawValues.Count ? actual.RawValues[r] : [];
                for (var c = 0; c < Math.Max(b.Count, a.Count); c++)
                    if ((c < b.Count ? b[c] : "{}") != (c < a.Count ? a[c] : "{}"))
                    { Diverged($"value r{r + 1}c{c + 1}: expected=[{(c < b.Count ? b[c][..Math.Min(80, b[c].Length)] : "<absent>")}] actual=[{(c < a.Count ? a[c][..Math.Min(80, a[c].Length)] : "<absent>")}] insert_col={mutation.ColumnIndex}"); }
            }
            Diverged("value-grid");
        }
        if (before.RawCellData is not null)
        {
            var cells = before.RawCellData.Select(r => r.ToList()).ToList();
            Transform(cells, mutation, "{}", true);
            if (actual.RawCellData is null || !GridEquals(cells, actual.RawCellData))
            {
                // Google InheritFromBefore의 형식 상속은 전체 CellData를 정확히 재현하기 어렵다.
                // 삽입 열의 값만 검증하고 형식은 건너뛴다 — 기존 열의 형식은 여전히 전수 검증한다.
                if (mutation.Kind == SheetMutationKind.Insert)
                {
                    var index = mutation.ColumnIndex - 1;
                    var valuesOnly = cells.Select(r => r.Where((_, c) => c != index).ToList()).ToList();
                    var actualValuesOnly = (actual.RawCellData ?? []).Select(r => r.Where((_, c) => c != index).ToList()).ToList();
                    if (!GridEquals(valuesOnly, actualValuesOnly)) Diverged();
                }
                else Diverged();
            }
        }
        // Inverse structural operations preserve surviving column properties. We restore only
        // the deleted column's properties; unrelated dimension edits are never overwritten.
        // Foreign metadata on a structural edit is rejected by the Google adapter before mutation.
        if (mutation.Kind is SheetMutationKind.Bind or SheetMutationKind.Write
            && Json(before.OtherColumnMetadata) != Json(actual.OtherColumnMetadata)) Diverged();
    }

    private static void Diverged()
    {
        throw new ConflictException("시트가 작업 결과와 다릅니다. 사람의 변경을 덮어쓰지 않고 복구 기록을 보존합니다.");
    }
    private static void Diverged(string detail)
    {
        throw new ConflictException($"시트가 작업 결과와 다릅니다({detail}). 사람의 변경을 덮어쓰지 않고 복구 기록을 보존합니다.");
    }
    private static bool GridEquals(IEnumerable<IReadOnlyList<string>> left, IEnumerable<IReadOnlyList<string>> right)
    {
        static JsonArray Normalize(IEnumerable<IReadOnlyList<string>> grid)
        {
            var rows = grid.Select(r => r.Select(v => JsonNode.Parse(v))
                .Reverse().SkipWhile(v => v is JsonObject { Count: 0 }).Reverse().ToList()).ToList();
            while (rows.Count > 0 && rows[^1].Count == 0) rows.RemoveAt(rows.Count - 1);
            return new JsonArray(rows.Select(row => (JsonNode)new JsonArray(row.ToArray())).ToArray());
        }
        return JsonNode.DeepEquals(Normalize(left), Normalize(right));
    }
    private static void Transform(List<List<string>> grid, ManagedSheetMutation mutation, string empty, bool cellData = false)
    {
        var index = mutation.ColumnIndex - 1;
        if (mutation.Kind is SheetMutationKind.Insert or SheetMutationKind.Delete)
            for (var r = 0; r < grid.Count; r++)
                for (var c = 0; c < grid[r].Count; c++)
                {
                    if (mutation.Kind == SheetMutationKind.Delete && c == index) continue;
                    var node = System.Text.Json.Nodes.JsonNode.Parse(grid[r][c])!.AsObject();
                    var value = cellData ? node["userEnteredValue"] : node;
                    if (value?["formulaValue"]?.GetValue<string>() is { } formula)
                    {
                        // 구글은 삽입·삭제 시 모든 A1 참조를 위치 규칙으로 다시 쓴다(세로·가로 무관).
                        value["formulaValue"] = ShiftFormulaReferences(formula, mutation.ColumnIndex,
                            mutation.Kind == SheetMutationKind.Insert ? 1 : -1);
                        grid[r][c] = node.ToJsonString();
                    }
                }
        if (mutation.Kind == SheetMutationKind.Insert)
            foreach (var row in grid)
            {
                var inserted = empty;
                if (cellData && index > 0 && row.Count >= index)
                {
                    // 구글 InheritFromBefore는 이전 열의 전체 셀 데이터를 상속한다 — 값만 교체한다.
                    var prior = System.Text.Json.Nodes.JsonNode.Parse(row[index - 1])!.AsObject();
                    var template = new System.Text.Json.Nodes.JsonObject();
                    foreach (var property in prior)
                        if (property.Key != "userEnteredValue") template[property.Key] = property.Value?.DeepClone();
                    inserted = template.ToJsonString();
                }
                while (row.Count < index) row.Add(empty);
                row.Insert(index, inserted);
            }
        if (mutation.Kind == SheetMutationKind.Delete)
            foreach (var row in grid) if (row.Count > index) row.RemoveAt(index);
        if (mutation.Kind is not (SheetMutationKind.Write or SheetMutationKind.Insert)) return;
        for (var i = 0; i < mutation.RawValues.Count; i++)
        {
            while (grid.Count <= i) grid.Add([]);
            while (grid[i].Count <= index) grid[i].Add(empty);
            if (!cellData) grid[i][index] = mutation.RawValues[i];
            else
            {
                var cell = System.Text.Json.Nodes.JsonNode.Parse(grid[i][index])!.AsObject();
                cell.Remove("userEnteredValue");
                if (mutation.RawValues[i] != "{}") cell["userEnteredValue"] = System.Text.Json.Nodes.JsonNode.Parse(mutation.RawValues[i]);
                grid[i][index] = cell.ToJsonString();
            }
        }
    }

    internal static string ColumnLetter(int column)
    {
        var result = "";
        for (var n = column; n > 0; n = (n - 1) / 26) result = (char)('A' + (n - 1) % 26) + result;
        return result;
    }

    internal static bool IsOwnColumnTotal(string formula, int column)
    {
        var name = ColumnLetter(column);
        return System.Text.RegularExpressions.Regex.IsMatch(formula,
            $"^=(?:SUM\\(\\$?{name}\\$?\\d+:\\$?{name}\\$?\\d+\\)|COUNTIF\\(\\$?{name}\\$?\\d+:\\$?{name}\\$?\\d+,\\\"출석\\\"\\))$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

    private static string ShiftTotalFormula(string formula, int from, int to)
    {
        if (!IsOwnColumnTotal(formula, from))
            throw new ConflictException("지원하지 않는 수식 의존성이 있습니다. 열 변경 전에 확인하세요.");
        return System.Text.RegularExpressions.Regex.Replace(formula, @"(\$?)[A-Z]+(?=\$?\d)",
            match => match.Groups[1].Value + ColumnLetter(to), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    internal static int ColumnNumber(string letters)
    {
        var column = 0;
        foreach (var c in letters.ToUpperInvariant()) column = column * 26 + (c - 'A' + 1);
        return column;
    }

    /// <summary>삽입/열 삭제 시 구글과 동일한 A1 참조 이동: 삽입 지점 이후 열은 +1, 삭제 이후는 -1.
    /// 삭제 지점 열은 범위 시작(다음 열이 그 자리를 차지)만 허용하고 나머지는 되돌릴 수 없어 거부한다.
    /// 문자열 리터럴 안은 다시 쓰지 않는다.</summary>
    internal static string ShiftFormulaReferences(string formula, int boundary, int delta)
    {
        var reference = new System.Text.RegularExpressions.Regex(@"(\$?)([A-Za-z]{1,3})(\$?)(\d{1,7})");
        var parts = formula.Split('"');
        for (var i = 0; i < parts.Length; i += 2) // 따옴표 바깥 부분만 수식이다
        {
            parts[i] = reference.Replace(parts[i], match =>
            {
                var column = ColumnNumber(match.Groups[2].Value);
                var rangeStart = match.Index + match.Length < parts[i].Length && parts[i][match.Index + match.Length] == ':';
                int shifted;
                if (delta > 0) shifted = column >= boundary ? column + 1 : column;
                else if (column > boundary) shifted = column - 1;
                else if (column < boundary) shifted = column;
                else if (rangeStart) shifted = column;
                else throw new ConflictException("삭제되는 열을 직접 참조하는 수식은 되돌릴 수 없습니다. 열 변경 전에 확인하세요.");
                return match.Groups[1].Value + ColumnLetter(shifted) + match.Groups[3].Value + match.Groups[4].Value;
            });
        }
        return string.Join("\"", parts);
    }

    /// <summary>수식의 모든 A1 참조가 자기 열(세로 집계) 또는 자기 행(가로 월집계·행합계)에만 붙어 있으면
    /// 삽입·삭제 이동이 국소적이어서 안전하다. 다른 행·열을 건드리는 참조는 여전히 거부한다.</summary>
    internal static bool IsLocallyBoundFormula(string formula, int row, int column)
    {
        var reference = new System.Text.RegularExpressions.Regex(@"(\$?)([A-Za-z]{1,3})(\$?)(\d{1,7})");
        var parts = formula.Split('"');
        for (var i = 0; i < parts.Length; i += 2)
            foreach (var match in reference.Matches(parts[i]).Cast<System.Text.RegularExpressions.Match>())
            {
                if (ColumnNumber(match.Groups[2].Value) != column && int.Parse(match.Groups[4].Value) != row) return false;
            }
        return true;
    }

    internal static async Task RecoverAsync(IDbContextFactory<ApplicationDbContext> factory, ISheetClient client)
    {
        await using var db = await factory.CreateDbContextAsync(CancellationToken.None);
        foreach (var journal in await db.SheetEditJournals.ToListAsync())
        {
            await RestoreAsync(client, journal);
            db.SheetEditJournals.Remove(journal);
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    private static async Task RestoreAsync(ISheetClient client, SheetEditJournal journal)
    {
        var before = JsonSerializer.Deserialize<ManagedSheetTab>(journal.BeforeJson)!;
        var mutation = JsonSerializer.Deserialize<ManagedSheetMutation>(journal.MutationJson)!;
        var current = await client.GetManagedTabAsync(journal.SpreadsheetId, before.Name, CancellationToken.None);
        // A previous rollback can leave only a newer bookkeeping marker. Equal data/identities
        // prove the preimage is intact; never rewrite the sheet merely to reset that marker.
        if (current.LastMutation != mutation.OperationId
            && Json(current with { LastMutation = before.LastMutation }) == Json(before)) return;
        if (current.LastMutation == mutation.OperationId)
        {
            RequireOwnedPostimage(before, current, mutation);
            await client.RestoreManagedTabAsync(journal.SpreadsheetId, before, current, mutation, CancellationToken.None);
            current = await client.GetManagedTabAsync(journal.SpreadsheetId, before.Name, CancellationToken.None);
        }
        if (Json(current with { LastMutation = before.LastMutation }) != Json(before))
            throw new ConflictException("시트 복구 상태를 확인할 수 없습니다. 추가 변경/동기화를 중단했습니다. 원본과 복구 기록을 확인하세요.");
    }

    internal static async Task CommitAsync(ApplicationDbContext db, ISheetClient client, string spreadsheetId,
        ManagedSheetTab before, ManagedSheetMutation mutation, Func<Task> changeDb)
    {
        var journal = new SheetEditJournal { Id = mutation.OperationId, SpreadsheetId = spreadsheetId,
            BeforeJson = Json(before), MutationJson = Json(mutation) };
        db.SheetEditJournals.Add(journal);
        await db.SaveChangesAsync(CancellationToken.None); // before making ANY remote change
        try
        {
            await client.ApplyManagedMutationAsync(spreadsheetId, before, mutation, CancellationToken.None);
            var after = await client.GetManagedTabAsync(spreadsheetId, before.Name, CancellationToken.None);
            RequireOwnedPostimage(before, after, mutation);
            await AttendanceCommitGate.WaitAsync(CancellationToken.None);
            try
            {
            await using var transaction = await db.Database.BeginTransactionAsync(CancellationToken.None);
            await changeDb();
            db.SheetEditJournals.Remove(journal);
            await db.SaveChangesAsync(CancellationToken.None);
            await AdminActivityService.RebuildMarks(db);
            await transaction.CommitAsync(CancellationToken.None); // domain changes + projection + journal removal are atomic
            }
            finally { AttendanceCommitGate.Release(); }
        }
        catch (Exception original)
        {
            db.ChangeTracker.Clear();
            try
            {
                await RestoreAsync(client, journal);
                db.SheetEditJournals.Remove(journal);
                await db.SaveChangesAsync(CancellationToken.None);
            }
            catch (Exception recovery)
            {
                throw new AggregateException("시트 변경 실패 및 복구 실패. 복구 기록을 보존했으며 추가 동기화 전에 복구가 필요합니다.", original, recovery);
            }
            throw;
        }
    }
}
