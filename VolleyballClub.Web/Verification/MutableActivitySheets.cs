using System.Text.Json;
using VolleyballClub.Application.Abstractions;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Application.Exceptions;
using VolleyballClub.Infrastructure.Services;

namespace VolleyballClub.Web.Verification;

// In-memory Google boundary only. All sync, journaling and application mutations remain real.
internal sealed class MutableActivitySheets : ISheetClient
{
    private readonly object gate = new();
    private readonly Dictionary<(string Document, string Tab), ManagedSheetTab> tabs = [];
    private string? fault;
    private int applied, restored, failed;
    internal const string First = "verification-activity-first";
    internal const string Second = "verification-activity-second";
    private static string Json<T>(T value) => JsonSerializer.Serialize(value);
    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(Json(value))!;
    private static string Raw(string value) => value.Length == 0 ? "{}" : Json(new Dictionary<string, object>
        { [value.StartsWith('=') ? "formulaValue" : "stringValue"] = value });
    private static string Text(string value)
    {
        using var doc = JsonDocument.Parse(value);
        var cell = doc.RootElement.EnumerateObject().FirstOrDefault().Value;
        return cell.ValueKind == JsonValueKind.Undefined ? "" : cell.ToString();
    }
    internal void Seed(string document, SheetTab tab)
    {
        lock (gate)
        {
            RequireDocument(document);
            Store(document, new(tab.Name, [], tab.Rows.Select(r => (IReadOnlyList<string>)r.Select(Raw).ToArray()).ToArray(), new Dictionary<int, string>()));
        }
    }
    private static void RequireDocument(string document)
    {
        if (document is not (First or Second)) throw new InvalidOperationException("Only synthetic verification documents are allowed.");
    }
    private ManagedSheetTab Read(string document, string tab)
    {
        RequireDocument(document);
        return tabs[(document, tab)];
    }
    private void Store(string document, ManagedSheetTab tab)
    {
        var raw = tab.RawValues.Select(r => (IReadOnlyList<string>)r.Reverse().SkipWhile(v => v == "{}").Reverse().ToArray()).ToList();
        while (raw.Count > 0 && raw[^1].Count == 0) raw.RemoveAt(raw.Count - 1);
        tabs[(document, tab.Name)] = Copy(tab with { RawValues = raw, Rows = raw.Select(r => (IReadOnlyList<string>)r.Select(Text).ToArray()).ToArray() });
    }
    internal void Arm(string mode)
    {
        if (mode is not ("beforeApply" or "afterApply")) throw new RuleViolationException("Unknown verification fault.");
        lock (gate) fault = mode;
    }
    private void Fail(string mode)
    {
        if (fault != mode) return;
        fault = null; failed++;
        throw new IOException($"Synthetic one-shot source failure: {mode}");
    }
    internal object State()
    {
        lock (gate) return new { applied, restored, failed, armedFault = fault,
            documents = tabs.OrderBy(t => t.Key.Document).ThenBy(t => t.Key.Tab)
                .Select(t => new { document = t.Key.Document, tab = Copy(t.Value) }).ToArray() };
    }
    public Task<IReadOnlyList<SheetTab>> GetTabsAsync(string spreadsheetId, IEnumerable<string>? tabNames = null, CancellationToken ct = default)
    {
        lock (gate)
        {
            RequireDocument(spreadsheetId);
            return Task.FromResult<IReadOnlyList<SheetTab>>(tabs.Where(t => t.Key.Document == spreadsheetId && (tabNames is null || tabNames.Contains(t.Key.Tab)))
                .Select(t => new SheetTab(t.Value.Name, Copy(t.Value.Rows))).ToArray());
        }
    }
    public Task<ManagedSheetTab> GetManagedTabAsync(string spreadsheetId, string tabName, CancellationToken ct = default)
    {
        lock (gate) return Task.FromResult(Copy(Read(spreadsheetId, tabName)));
    }
    private static int MemberEnd(ManagedSheetTab tab)
    {
        var header = tab.Rows.Count > 0 && tab.Rows[0].Any(v => v.Trim().StartsWith("활동 ")) ? 1 : 0;
        for (var row = header + 1; row < tab.Rows.Count; row++)
        {
            var label = tab.Rows[row].Count > 0 ? tab.Rows[row][0].Trim() : "";
            if (label is "총 참여자 수" or "탈퇴/퇴출자") return row;
        }
        return tab.Rows.Count;
    }

    public Task ApplyManagedMutationAsync(string spreadsheetId, ManagedSheetTab expected, ManagedSheetMutation mutation, CancellationToken ct = default)
    {
        lock (gate)
        {
            if (Json(Read(spreadsheetId, expected.Name)) != Json(expected)) throw new ConflictException("Changed synthetic source");
            Fail("beforeApply");
            var values = expected.RawValues.Select(r => r.ToList()).ToList();
            if (mutation.Kind == SheetMutationKind.InsertMemberRow)
            {
                // Same sheet behavior as production Google: append member row, widen totals ranges by one.
                var end = MemberEnd(expected);
                var row = Enumerable.Repeat("{}", Math.Max(1, values.Max(r => r.Count))).ToList();
                row[0] = mutation.RawValues[0];
                values.Insert(end, row);
                var totals = values[end + 1];
                for (var c = 0; c < totals.Count; c++)
                {
                    using var cell = JsonDocument.Parse(totals[c]);
                    if (!cell.RootElement.TryGetProperty("formulaValue", out var formula)) continue;
                    totals[c] = Raw(System.Text.RegularExpressions.Regex.Replace(formula.GetString()!,
                        @"(\$?)([A-Z]+)(\$?)" + end + @"(\)|,)",
                        match => $"{match.Groups[1].Value}{match.Groups[2].Value}{match.Groups[3].Value}{end + 1}{match.Groups[4].Value}",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase));
                }
                Store(spreadsheetId, new(expected.Name, [], values, expected.ColumnKeys.ToDictionary(k => k.Key, k => k.Value), mutation.OperationId));
                applied++;
                Fail("afterApply");
                return Task.CompletedTask;
            }
            var index = mutation.ColumnIndex - 1;
            var keys = new Dictionary<int, string>();
            foreach (var key in expected.ColumnKeys)
            {
                if (mutation.Kind == SheetMutationKind.Delete && key.Key == mutation.ColumnIndex) continue;
                var position = key.Key + (mutation.Kind == SheetMutationKind.Insert && key.Key >= mutation.ColumnIndex ? 1
                    : mutation.Kind == SheetMutationKind.Delete && key.Key > mutation.ColumnIndex ? -1 : 0);
                keys[position] = key.Value;
            }
            // Sheets rewrites all A1 references when columns move; preserve typed formula cells.
            if (mutation.Kind is SheetMutationKind.Insert or SheetMutationKind.Delete)
                foreach (var row in values)
                    for (var c = 0; c < row.Count; c++)
                    {
                        if (mutation.Kind == SheetMutationKind.Delete && c == index) continue;
                        using var cell = JsonDocument.Parse(row[c]);
                        if (cell.RootElement.TryGetProperty("formulaValue", out var formula))
                            row[c] = Raw(ManagedSheetEdits.ShiftFormulaReferences(formula.GetString()!,
                                mutation.ColumnIndex, mutation.Kind == SheetMutationKind.Insert ? 1 : -1));
                    }
            if (mutation.Kind == SheetMutationKind.Insert)
                foreach (var row in values) { while (row.Count < index) row.Add("{}"); row.Insert(index, "{}"); }
            if (mutation.Kind == SheetMutationKind.Delete)
                foreach (var row in values) if (row.Count > index) row.RemoveAt(index);
            if (mutation.Kind is SheetMutationKind.Insert or SheetMutationKind.Write)
                for (var i = 0; i < mutation.RawValues.Count; i++)
                {
                    while (values.Count <= i) values.Add([]);
                    while (values[i].Count <= index) values[i].Add("{}");
                    values[i][index] = mutation.RawValues[i];
                }
            if (mutation.Kind is SheetMutationKind.Insert or SheetMutationKind.Bind) keys[mutation.ColumnIndex] = mutation.SourceKey;
            Store(spreadsheetId, new(expected.Name, [], values, keys, mutation.OperationId));
            applied++;
            Fail("afterApply");
            return Task.CompletedTask;
        }
    }
    private static string ColumnLetter(int column)
    {
        var result = "";
        for (var n = column; n > 0; n = (n - 1) / 26) result = (char)('A' + (n - 1) % 26) + result;
        return result;
    }
    public Task RestoreManagedTabAsync(string spreadsheetId, ManagedSheetTab before, ManagedSheetTab expected, ManagedSheetMutation mutation, CancellationToken ct = default)
    {
        lock (gate)
        {
            if (Json(Read(spreadsheetId, before.Name)) != Json(expected)) throw new ConflictException("Changed synthetic postimage");
            Store(spreadsheetId, before); restored++;
            return Task.CompletedTask;
        }
    }
    // Legacy exports must update the same state consumed by managed reads and old GetTabs callers.
    private Task Legacy(string document, string tabName, int column, SheetColumn? cells, SheetMutationKind kind)
    {
        lock (gate)
        {
            var before = Read(document, tabName);
            var raw = before.RawValues.Select(r => r.Count >= column ? r[column - 1] : "{}").ToList();
            if (cells is not null)
                for (var i = 0; i < cells.Cells.Count; i++)
                {
                    var row = cells.FirstRowIndex - 1 + i;
                    while (raw.Count <= row) raw.Add("{}");
                    raw[row] = Raw(cells.Cells[i]);
                }
            return ApplyManagedMutationAsync(document, before, new(Guid.NewGuid().ToString("N"), kind, column,
                before.ColumnKeys.GetValueOrDefault(column) ?? Guid.NewGuid().ToString("N"), raw));
        }
    }
    public Task WriteColumnAsync(string id, string tab, int index, SheetColumn column, CancellationToken ct = default) => Legacy(id, tab, index, column, SheetMutationKind.Write);
    public Task InsertColumnAsync(string id, string tab, int index, SheetColumn column, CancellationToken ct = default) => Legacy(id, tab, index, column, SheetMutationKind.Insert);
    public Task DeleteColumnAsync(string id, string tab, int index, CancellationToken ct = default) => Legacy(id, tab, index, null, SheetMutationKind.Delete);
}
