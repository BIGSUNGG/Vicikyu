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
    internal static string Raw(string value) => value == "1" ? "{\"numberValue\":1}"
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
        if (actual.LastMutation != mutation.OperationId || Json(keys.OrderBy(k => k.Key)) != Json(actual.ColumnKeys.OrderBy(k => k.Key))
            || !GridEquals(values, actual.RawValues)) Diverged();
        if (before.RawCellData is not null)
        {
            var cells = before.RawCellData.Select(r => r.ToList()).ToList();
            Transform(cells, mutation, "{}", true);
            if (actual.RawCellData is null || !GridEquals(cells, actual.RawCellData)) Diverged();
        }
        // Inverse structural operations preserve surviving column properties. We restore only
        // the deleted column's properties; unrelated dimension edits are never overwritten.
        // Foreign metadata on a structural edit is rejected by the Google adapter before mutation.
        if (mutation.Kind is SheetMutationKind.Bind or SheetMutationKind.Write
            && Json(before.OtherColumnMetadata) != Json(actual.OtherColumnMetadata)) Diverged();
    }

    private static void Diverged() => throw new ConflictException("시트가 작업 결과와 다릅니다. 사람의 변경을 덮어쓰지 않고 복구 기록을 보존합니다.");
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
                        var shifted = c + (mutation.Kind == SheetMutationKind.Insert && c >= index ? 1
                            : mutation.Kind == SheetMutationKind.Delete && c > index ? -1 : 0);
                        if (shifted != c) value["formulaValue"] = ShiftTotalFormula(formula, c + 1, shifted + 1);
                        grid[r][c] = node.ToJsonString();
                    }
                }
        if (mutation.Kind == SheetMutationKind.Insert)
            foreach (var row in grid)
            {
                var inserted = empty;
                if (cellData && index > 0 && row.Count >= index)
                {
                    var prior = System.Text.Json.Nodes.JsonNode.Parse(row[index - 1])!.AsObject();
                    var template = new System.Text.Json.Nodes.JsonObject();
                    foreach (var property in new[] { "userEnteredFormat", "dataValidation" })
                        if (prior[property] is { } part) template[property] = part.DeepClone();
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
