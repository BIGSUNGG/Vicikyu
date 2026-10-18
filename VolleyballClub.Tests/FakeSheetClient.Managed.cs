using System.Text.Json;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Application.Exceptions;
using VolleyballClub.Infrastructure.Services;

namespace VolleyballClub.Tests;

public sealed partial class FakeSheetClient
{
    public Dictionary<string, List<SheetTab>> Documents { get; } = [];
    private readonly Dictionary<(string, string), Dictionary<int, string>> keys = [];
    private readonly Dictionary<(string, string), string?> markers = [];
    private readonly Dictionary<(string, string), IReadOnlyList<IReadOnlyList<string>>> rawCells = [];
    public Action<ManagedSheetMutation>? BeforeManagedApply { get; set; }
    public Action<ManagedSheetMutation>? AfterManagedApply { get; set; }
    public Action? BeforeRestore { get; set; }
    public Action<string, string>? BeforeManagedRead { get; set; }
    public int RestoreCount { get; private set; }
    public void SetManagedTab(string document, ManagedSheetTab tab) => Store(document, tab);
    public List<ManagedSheetMutation> ManagedWrites { get; } = [];
    private List<SheetTab> Document(string id) => Documents.GetValueOrDefault(id) ?? Tabs;
    private static string Json<T>(T value) => JsonSerializer.Serialize(value);
    private static string Raw(string value) => value.Length == 0 ? "{}" : Json(new Dictionary<string, object> { [value.StartsWith('=') ? "formulaValue" : "stringValue"] = value });
    private static string Text(string value)
    {
        using var doc = JsonDocument.Parse(value);
        var cell = doc.RootElement.EnumerateObject().FirstOrDefault().Value;
        return cell.ValueKind == JsonValueKind.Undefined ? "" : cell.ToString();
    }
    public void SetTab(string document, SheetTab tab)
    {
        var list = Document(document); var index = list.FindIndex(t => t.Name == tab.Name);
        if (index < 0) list.Add(tab); else list[index] = tab;
        rawCells.Remove((document, tab.Name));
    }
    public Task<ManagedSheetTab> GetManagedTabAsync(string spreadsheetId, string tabName, CancellationToken ct = default)
    {
        BeforeManagedRead?.Invoke(spreadsheetId, tabName);
        var tab = Document(spreadsheetId).Single(t => t.Name == tabName);
        var k = (spreadsheetId, tabName);
        var raw = rawCells.GetValueOrDefault(k) ?? tab.Rows.Select(r => (IReadOnlyList<string>)r.Select(Raw).ToList()).ToList();
        return Task.FromResult(new ManagedSheetTab(tab.Name, tab.Rows, raw,
            keys.GetValueOrDefault(k)?.OrderBy(p => p.Key).ToDictionary(p => p.Key, p => p.Value) ?? [], markers.GetValueOrDefault(k)));
    }
    private void Store(string document, ManagedSheetTab tab)
    {
        var raw = tab.RawValues.Select(r => (IReadOnlyList<string>)r.Reverse().SkipWhile(v => v == "{}").Reverse().ToList()).ToList();
        while (raw.Count > 0 && raw[^1].Count == 0) raw.RemoveAt(raw.Count - 1);
        SetTab(document, new SheetTab(tab.Name, raw.Select(r => (IReadOnlyList<string>)r.Select(Text).ToList()).ToList()));
        var k = (document, tab.Name); rawCells[k] = raw;
        keys[k] = tab.ColumnKeys.ToDictionary(p => p.Key, p => p.Value); markers[k] = tab.LastMutation;
    }
    public async Task ApplyManagedMutationAsync(string spreadsheetId, ManagedSheetTab expected, ManagedSheetMutation mutation, CancellationToken ct = default)
    {
        if (Json(await GetManagedTabAsync(spreadsheetId, expected.Name, ct)) != Json(expected)) throw new ConflictException("Changed source");
        BeforeManagedApply?.Invoke(mutation);
        var values = expected.RawValues.Select(r => r.ToList()).ToList();
        if (mutation.Kind == SheetMutationKind.InsertMemberRow)
        {
            // Independently model the sheet: append the member row, then widen every totals-range end by one.
            var end = SemesterTabFormat.MemberRegion(new(expected.Name, expected.Rows)).End;
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
            ManagedWrites.Add(mutation);
            AfterManagedApply?.Invoke(mutation);
            return;
        }
        var index = mutation.ColumnIndex - 1;
        var changedKeys = new Dictionary<int, string>();
        foreach (var key in expected.ColumnKeys)
        {
            if (mutation.Kind == SheetMutationKind.Delete && key.Key == mutation.ColumnIndex) continue;
            var position = key.Key + (mutation.Kind == SheetMutationKind.Insert && key.Key >= mutation.ColumnIndex ? 1
                : mutation.Kind == SheetMutationKind.Delete && key.Key > mutation.ColumnIndex ? -1 : 0);
            changedKeys[position] = key.Value;
        }
        // Independently model Sheets' reference rewrites, not the production ownership checker.
        if (mutation.Kind is SheetMutationKind.Insert or SheetMutationKind.Delete)
            foreach (var row in values)
                for (var c = 0; c < row.Count; c++)
                {
                    if (mutation.Kind == SheetMutationKind.Delete && c == index) continue;
                    var shifted = c + (mutation.Kind == SheetMutationKind.Insert && c >= index ? 1
                        : mutation.Kind == SheetMutationKind.Delete && c > index ? -1 : 0);
                    if (shifted == c) continue;
                    using var cell = JsonDocument.Parse(row[c]);
                    if (cell.RootElement.TryGetProperty("formulaValue", out var formula))
                    {
                        var from = ColumnLetter(c + 1);
                        var text = formula.GetString()!;
                        if (!System.Text.RegularExpressions.Regex.IsMatch(text,
                            $"^=(?:SUM\\(\\$?{from}\\$?\\d+:\\$?{from}\\$?\\d+\\)|COUNTIF\\(\\$?{from}\\$?\\d+:\\$?{from}\\$?\\d+,\\\"출석\\\"\\))$"))
                            throw new ConflictException("Unsupported fake formula dependency");
                        row[c] = Raw(System.Text.RegularExpressions.Regex.Replace(text, @"(\$?)[A-Z]+(?=\$?\d)",
                            match => match.Groups[1].Value + ColumnLetter(shifted + 1)));
                    }
                }
        if (mutation.Kind == SheetMutationKind.Insert)
            foreach (var row in values) { while (row.Count < index) row.Add("{}"); row.Insert(index, "{}"); }
        if (mutation.Kind == SheetMutationKind.Delete)
            foreach (var row in values) if (row.Count > index) row.RemoveAt(index);
        if (mutation.Kind is SheetMutationKind.Insert or SheetMutationKind.Write)
        {
            var handler = mutation.Kind == SheetMutationKind.Insert ? InsertHandler : WriteHandler;
            if (handler is not null) await handler(expected.Name, mutation.ColumnIndex, new SheetColumn(1, mutation.RawValues.Select(Text).ToList()));
            for (var i = 0; i < mutation.RawValues.Count; i++)
            {
                while (values.Count <= i) values.Add([]);
                while (values[i].Count <= index) values[i].Add("{}");
                values[i][index] = mutation.RawValues[i];
            }
            Writes.Add((expected.Name, mutation.Kind == SheetMutationKind.Insert, mutation.ColumnIndex,
                new SheetColumn(1, mutation.RawValues.Select(Text).ToList())));
        }
        if (mutation.Kind is SheetMutationKind.Insert or SheetMutationKind.Bind) changedKeys[mutation.ColumnIndex] = mutation.SourceKey;
        if (mutation.Kind == SheetMutationKind.Delete) Deletions.Add((expected.Name, mutation.ColumnIndex));
        Store(spreadsheetId, new ManagedSheetTab(expected.Name, [], values, changedKeys, mutation.OperationId));
        ManagedWrites.Add(mutation);
        AfterManagedApply?.Invoke(mutation);
    }
    public async Task RestoreManagedTabAsync(string spreadsheetId, ManagedSheetTab before, ManagedSheetTab expected, ManagedSheetMutation mutation, CancellationToken ct = default)
    {
        BeforeRestore?.Invoke();
        if (Json(await GetManagedTabAsync(spreadsheetId, before.Name, ct)) != Json(expected)) throw new ConflictException("Changed postimage");
        Store(spreadsheetId, before);
        RestoreCount++;
    }
    private static string ColumnLetter(int column)
    {
        var result = "";
        for (var n = column; n > 0; n = (n - 1) / 26) result = (char)('A' + (n - 1) % 26) + result;
        return result;
    }
}
