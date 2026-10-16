using System.Text.Json;
using Google.Apis.Sheets.v4.Data;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Application.Exceptions;

namespace VolleyballClub.Infrastructure.Services;

public sealed partial class GoogleSheetClient
{
    private const string SourceMetadata = "vicikyu.activity-source";
    private const string OperationMetadata = "vicikyu.last-edit";

    private async Task<Sheet> ReadManagedAsync(string spreadsheetId, string tabName, CancellationToken ct)
    {
        var svc = await GetServiceAsync(ct);
        var request = svc.Spreadsheets.Get(spreadsheetId);
        request.Ranges = new[] { QuoteTab(tabName) };
        request.IncludeGridData = true;
        request.Fields = "sheets(properties(sheetId,title,gridProperties),developerMetadata,data(startRow,startColumn,columnMetadata(pixelSize,hiddenByUser),rowData(values(userEnteredValue,userEnteredFormat,note,dataValidation,textFormatRuns))))";
        return (await request.ExecuteAsync(ct)).Sheets.Single(s => s.Properties.Title == tabName);
    }

    private static ManagedSheetTab Snapshot(Sheet sheet)
    {
        var values = sheet.Data?.FirstOrDefault()?.RowData ?? [];
        var raw = values.Select(r => (IReadOnlyList<string>)(r.Values ?? []).Select(c =>
            c.UserEnteredValue is null ? "{}" : JsonSerializer.Serialize(c.UserEnteredValue,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull })).ToList()).ToList();
        // Normalize trailing empties; Google omits them in responses.
        raw = raw.Select(r => (IReadOnlyList<string>)r.Reverse().SkipWhile(v => v == "{}").Reverse().ToList()).ToList();
        while (raw.Count > 0 && raw[^1].Count == 0) raw.RemoveAt(raw.Count - 1);
        var keys = (sheet.DeveloperMetadata ?? []).Where(m => m.MetadataKey == SourceMetadata)
            .ToDictionary(m => m.Location.DimensionRange.StartIndex!.Value + 1, m => m.MetadataValue);
        return new ManagedSheetTab(sheet.Properties.Title,
            raw.Select(r => (IReadOnlyList<string>)r.Select(RawText).ToList()).ToList(), raw,
            keys.OrderBy(k => k.Key).ToDictionary(k => k.Key, k => k.Value),
            sheet.DeveloperMetadata?.SingleOrDefault(m => m.MetadataKey == OperationMetadata)?.MetadataValue,
            values.Select(r => (IReadOnlyList<string>)(r.Values ?? []).Select(c => JsonSerializer.Serialize(c,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull })).ToList()).ToList(),
            sheet.Data?.FirstOrDefault()?.ColumnMetadata?.Select(p => JsonSerializer.Serialize(p)).ToList(),
            (sheet.DeveloperMetadata ?? []).Where(m => m.MetadataKey is not SourceMetadata and not OperationMetadata
                && m.Location?.DimensionRange is not null).Select(m => JsonSerializer.Serialize(m)).OrderBy(m => m).ToList());
    }

    private static string RawText(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject().FirstOrDefault().Value.ValueKind == JsonValueKind.Undefined
            ? "" : doc.RootElement.EnumerateObject().First().Value.ToString();
    }

    public async Task<ManagedSheetTab> GetManagedTabAsync(string spreadsheetId, string tabName, CancellationToken ct = default) =>
        Snapshot(await ReadManagedAsync(spreadsheetId, tabName, ct));

    private static Request RemoveMetadata(int id) => new()
    {
        DeleteDeveloperMetadata = new DeleteDeveloperMetadataRequest
        {
            DataFilter = new DataFilter { DeveloperMetadataLookup = new DeveloperMetadataLookup { MetadataId = id } }
        }
    };
    private static Request AddMetadata(int sheetId, string key, string value, int? column = null) => new()
    {
        CreateDeveloperMetadata = new CreateDeveloperMetadataRequest
        {
            DeveloperMetadata = new DeveloperMetadata
            {
                MetadataKey = key, MetadataValue = value, Visibility = "DOCUMENT",
                Location = column is null ? new DeveloperMetadataLocation { SheetId = sheetId }
                    : new DeveloperMetadataLocation { DimensionRange = Dimension(sheetId, column.Value) }
            }
        }
    };
    private static DimensionRange Dimension(int sheetId, int column) => new()
        { SheetId = sheetId, Dimension = "COLUMNS", StartIndex = column - 1, EndIndex = column };
    private static Request Insert(int sheetId, int column) => new()
        { InsertDimension = new InsertDimensionRequest { Range = Dimension(sheetId, column), InheritFromBefore = column > 1 } };
    private static Request Insert(Sheet sheet, int column) => column > sheet.Properties.GridProperties.ColumnCount
        ? new Request { AppendDimension = new AppendDimensionRequest { SheetId = sheet.Properties.SheetId, Dimension = "COLUMNS", Length = 1 } }
        : Insert(sheet.Properties.SheetId!.Value, column);
    private static Request Delete(int sheetId, int column) => new()
        { DeleteDimension = new DeleteDimensionRequest { Range = Dimension(sheetId, column) } };
    private static ExtendedValue? Decode(string raw) => raw == "{}" ? null : JsonSerializer.Deserialize<ExtendedValue>(raw, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    private static Request Write(int sheetId, int column, IReadOnlyList<string> raw) => new()
    {
        UpdateCells = new UpdateCellsRequest
        {
            Start = new GridCoordinate { SheetId = sheetId, RowIndex = 0, ColumnIndex = column - 1 },
            Fields = "userEnteredValue", Rows = raw.Select(r => new RowData
                { Values = new List<CellData> { new() { UserEnteredValue = Decode(r) } } }).ToList()
        }
    };
    private static void Check(ManagedSheetTab expected, ManagedSheetTab actual)
    {
        if (ManagedSheetEdits.Json(expected) != ManagedSheetEdits.Json(actual))
            throw new ConflictException("시트가 변경되었습니다. 새로고침 후 다시 시도하세요.");
    }

    private async Task CheckStructuralSafetyAsync(string spreadsheetId, string tabName, int? ownedFormulaColumn, CancellationToken ct)
    {
        // The attendance format's own-column totals have reversible reference shifts.
        // A general spreadsheet formula/metadata engine is intentionally not implemented.
        var svc = await GetServiceAsync(ct);
        var request = svc.Spreadsheets.Get(spreadsheetId);
        request.IncludeGridData = true;
        request.Fields = "namedRanges(range),sheets(properties(sheetId,title),developerMetadata,data(startRow,startColumn,rowData(values(userEnteredValue))))";
        var workbook = await request.ExecuteAsync(ct);
        var target = workbook.Sheets.Single(s => s.Properties.Title == tabName);
        if ((workbook.NamedRanges ?? []).Any(n => n.Range.SheetId == target.Properties.SheetId)
            || (target.DeveloperMetadata ?? []).Any(m => m.MetadataKey is not SourceMetadata and not OperationMetadata && m.Location?.DimensionRange is not null))
            throw new ConflictException("이 탭의 이름 지정 범위/외부 메타데이터는 안전한 열 복구를 지원하지 않습니다.");
        foreach (var tab in workbook.Sheets)
            foreach (var grid in tab.Data ?? [])
                foreach (var row in grid.RowData ?? [])
                    for (var c = 0; c < (row.Values?.Count ?? 0); c++)
                    {
                        var formula = row.Values![c].UserEnteredValue?.FormulaValue;
                        if (formula is null) continue;
                        var column = (grid.StartColumn ?? 0) + c + 1;
                        var own = tab.Properties.Title == tabName;
                        if (own && (column == ownedFormulaColumn || ManagedSheetEdits.IsOwnColumnTotal(formula, column))) continue;
                        if (!own && !formula.Contains("'" + tabName.Replace("'", "''") + "'!", StringComparison.OrdinalIgnoreCase)
                            && !formula.Contains(tabName + "!", StringComparison.OrdinalIgnoreCase)) continue;
                        throw new ConflictException("출석 집계 외의 수식 의존성이 있습니다. 열 변경 전에 수식 의존성을 해소하세요.");
                    }
    }

    public async Task ApplyManagedMutationAsync(string spreadsheetId, ManagedSheetTab expected,
        ManagedSheetMutation mutation, CancellationToken ct = default)
    {
        if (mutation.Kind is SheetMutationKind.Insert or SheetMutationKind.Delete)
            await CheckStructuralSafetyAsync(spreadsheetId, expected.Name,
                mutation.Kind == SheetMutationKind.Delete ? mutation.ColumnIndex : null, ct);
        var sheet = await ReadManagedAsync(spreadsheetId, expected.Name, ct);
        Check(expected, Snapshot(sheet));
        var id = sheet.Properties.SheetId!.Value;
        var requests = new List<Request>();
        foreach (var marker in (sheet.DeveloperMetadata ?? []).Where(m => m.MetadataKey == OperationMetadata))
            requests.Add(RemoveMetadata(marker.MetadataId!.Value));
        if (mutation.Kind == SheetMutationKind.Insert) requests.Add(Insert(sheet, mutation.ColumnIndex));
        if (mutation.Kind == SheetMutationKind.Delete) requests.Add(Delete(id, mutation.ColumnIndex));
        if (mutation.Kind == SheetMutationKind.Insert)
        {
            var rows = new List<RowData>();
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            for (var r = 0; r < Math.Max(expected.RawCellData?.Count ?? 0, mutation.RawValues.Count); r++)
            {
                var previous = expected.RawCellData is { } data && r < data.Count && mutation.ColumnIndex > 1 && data[r].Count >= mutation.ColumnIndex - 1
                    ? JsonSerializer.Deserialize<CellData>(data[r][mutation.ColumnIndex - 2], options) : null;
                rows.Add(new RowData { Values = new List<CellData> { new()
                {
                    UserEnteredValue = r < mutation.RawValues.Count ? Decode(mutation.RawValues[r]) : null,
                    UserEnteredFormat = previous?.UserEnteredFormat, DataValidation = previous?.DataValidation
                } } });
            }
            requests.Add(new Request { UpdateCells = new UpdateCellsRequest
            {
                Range = new GridRange { SheetId = id, StartColumnIndex = mutation.ColumnIndex - 1, EndColumnIndex = mutation.ColumnIndex },
                Fields = "userEnteredValue,userEnteredFormat,note,dataValidation,textFormatRuns", Rows = rows
            } });
        }
        else if (mutation.Kind == SheetMutationKind.Write)
            requests.Add(Write(id, mutation.ColumnIndex, mutation.RawValues));
        if (mutation.Kind is SheetMutationKind.Insert or SheetMutationKind.Bind)
            requests.Add(AddMetadata(id, SourceMetadata, mutation.SourceKey, mutation.ColumnIndex));
        requests.Add(AddMetadata(id, OperationMetadata, mutation.OperationId));
        var svc = await GetServiceAsync(ct);
        await svc.Spreadsheets.BatchUpdate(new BatchUpdateSpreadsheetRequest { Requests = requests }, spreadsheetId).ExecuteAsync(ct);
    }

    public async Task RestoreManagedTabAsync(string spreadsheetId, ManagedSheetTab before, ManagedSheetTab expected,
        ManagedSheetMutation mutation, CancellationToken ct = default)
    {
        ManagedSheetEdits.RequireOwnedPostimage(before, expected, mutation);
        if (mutation.Kind is SheetMutationKind.Insert or SheetMutationKind.Delete)
            await CheckStructuralSafetyAsync(spreadsheetId, before.Name,
                mutation.Kind == SheetMutationKind.Insert ? mutation.ColumnIndex : null, ct);
        var sheet = await ReadManagedAsync(spreadsheetId, before.Name, ct);
        Check(expected, Snapshot(sheet));
        if (expected.LastMutation != mutation.OperationId) throw new ConflictException("복구할 시트 변경 식별자가 다릅니다.");
        var id = sheet.Properties.SheetId!.Value;
        var requests = new List<Request>();
        if (mutation.Kind == SheetMutationKind.Insert)
        {
            var inserted = expected.ColumnKeys.SingleOrDefault(k => k.Value == mutation.SourceKey);
            if (inserted.Key != mutation.ColumnIndex) throw new ConflictException("삽입한 열의 위치를 확인할 수 없습니다.");
            requests.Add(Delete(id, inserted.Key));
        }
        if (mutation.Kind == SheetMutationKind.Delete) requests.Add(Insert(sheet, mutation.ColumnIndex));
        // Restore the entire raw preimage: structural edits can rewrite formulas outside the edited column.
        requests.Add(new Request { UpdateCells = new UpdateCellsRequest
        {
            Range = new GridRange { SheetId = id }, Fields = "userEnteredValue,userEnteredFormat,note,dataValidation,textFormatRuns",
            Rows = before.RawCellData is not null ? before.RawCellData.Select(r => new RowData
                { Values = r.Select(v => JsonSerializer.Deserialize<CellData>(v, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!).ToList() }).ToList()
                : before.RawValues.Select(r => new RowData
                { Values = r.Select(v => new CellData { UserEnteredValue = Decode(v) }).ToList() }).ToList()
        }});
        foreach (var metadata in (sheet.DeveloperMetadata ?? []).Where(m => m.MetadataKey is SourceMetadata or OperationMetadata))
            if (!(mutation.Kind == SheetMutationKind.Insert && metadata.MetadataKey == SourceMetadata
                && metadata.MetadataValue == mutation.SourceKey)) requests.Add(RemoveMetadata(metadata.MetadataId!.Value));
        if (mutation.Kind == SheetMutationKind.Delete)
        {
            if (before.ColumnProperties is { } properties && properties.Count >= mutation.ColumnIndex)
                requests.Add(new Request { UpdateDimensionProperties = new UpdateDimensionPropertiesRequest
                { Range = Dimension(id, mutation.ColumnIndex), Fields = "pixelSize,hiddenByUser",
                    Properties = JsonSerializer.Deserialize<DimensionProperties>(properties[mutation.ColumnIndex - 1]) } });
            foreach (var rawMetadata in before.OtherColumnMetadata ?? [])
            {
                var metadata = JsonSerializer.Deserialize<DeveloperMetadata>(rawMetadata)!;
                if (metadata.Location.DimensionRange.StartIndex == mutation.ColumnIndex - 1)
                    requests.Add(new Request { CreateDeveloperMetadata = new CreateDeveloperMetadataRequest { DeveloperMetadata = metadata } });
            }
        }
        foreach (var key in before.ColumnKeys) requests.Add(AddMetadata(id, SourceMetadata, key.Value, key.Key));
        if (before.LastMutation is not null) requests.Add(AddMetadata(id, OperationMetadata, before.LastMutation));
        var svc = await GetServiceAsync(ct);
        await svc.Spreadsheets.BatchUpdate(new BatchUpdateSpreadsheetRequest { Requests = requests }, spreadsheetId).ExecuteAsync(ct);
    }
}
