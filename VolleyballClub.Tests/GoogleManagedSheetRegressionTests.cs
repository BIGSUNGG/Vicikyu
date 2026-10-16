using System.Text.Json;
using System.Text.Json.Nodes;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Application.Exceptions;

namespace VolleyballClub.Tests;

public sealed partial class GoogleManagedSheetTests
{
    [Theory]
    [InlineData(SheetMutationKind.Insert)]
    [InlineData(SheetMutationKind.Delete)]
    public async Task Countif_and_sum_totals_allow_structural_changes_and_exact_inverse_restore(SheetMutationKind kind)
    {
        var workbook = JsonNode.Parse(Sheet())!;
        var rows = workbook["sheets"]![0]!["data"]![0]!["rowData"]!.AsArray();
        rows[1]!["values"]![1]!["userEnteredValue"]!["formulaValue"] = "=COUNTIF(B3:B4,\"출석\")";
        rows[0]!["values"]!.AsArray().Add(JsonNode.Parse("{\"userEnteredValue\":{\"stringValue\":\"other\"}}"));
        rows[1]!["values"]!.AsArray().Add(JsonNode.Parse("{\"userEnteredValue\":{\"formulaValue\":\"=SUM(C3:C4)\"}}"));
        await using var db = new TestDb(); using var transport = new Transport { Spreadsheet = workbook.ToJsonString() };
        var client = Client(db, transport); var before = await client.GetManagedTabAsync("synthetic", "2026-2");
        var mutation = new ManagedSheetMutation("totals", kind, 2, kind == SheetMutationKind.Insert ? "new-source" : "source", []);
        await client.ApplyManagedMutationAsync("synthetic", before, mutation);
        Assert.Single(transport.Posts); transport.Posts.Clear();
        // Model Google's postimage independently: surviving own-column totals move with their columns.
        foreach (var row in rows)
        {
            var cells = row!["values"]!.AsArray();
            if (kind == SheetMutationKind.Insert) cells.Insert(1, new JsonObject()); else cells.RemoveAt(1);
        }
        if (kind == SheetMutationKind.Insert)
        {
            rows[1]!["values"]![2]!["userEnteredValue"]!["formulaValue"] = "=COUNTIF(C3:C4,\"출석\")";
            rows[1]!["values"]![3]!["userEnteredValue"]!["formulaValue"] = "=SUM(D3:D4)";
        }
        else rows[1]!["values"]![1]!["userEnteredValue"]!["formulaValue"] = "=SUM(B3:B4)";
        var metadata = workbook["sheets"]![0]!["developerMetadata"]!.AsArray();
        if (kind == SheetMutationKind.Delete) metadata.Clear();
        else
        {
            metadata[0]!["location"]!["dimensionRange"]!["startIndex"] = 2;
            metadata[0]!["location"]!["dimensionRange"]!["endIndex"] = 3;
            var added = metadata[0]!.DeepClone(); added["metadataId"] = 12; added["metadataValue"] = "new-source";
            added["location"]!["dimensionRange"]!["startIndex"] = 1; added["location"]!["dimensionRange"]!["endIndex"] = 2;
            metadata.Add(added);
        }
        metadata.Add(JsonNode.Parse("{\"metadataId\":11,\"metadataKey\":\"vicikyu.last-edit\",\"metadataValue\":\"totals\",\"visibility\":\"DOCUMENT\",\"location\":{\"sheetId\":7}}"));
        transport.Spreadsheet = workbook.ToJsonString();
        var after = await client.GetManagedTabAsync("synthetic", "2026-2");
        await client.RestoreManagedTabAsync("synthetic", before, after, mutation);
        using var restored = JsonDocument.Parse(Assert.Single(transport.Posts));
        var requests = restored.RootElement.GetProperty("requests").EnumerateArray().ToList();
        Assert.Contains(requests, r => r.TryGetProperty(kind == SheetMutationKind.Insert ? "deleteDimension" : "insertDimension", out _));
        var restoredCells = requests.Single(r => r.TryGetProperty("updateCells", out _)).GetProperty("updateCells").GetProperty("rows")[1].GetProperty("values");
        Assert.Equal("=COUNTIF(B3:B4,\"출석\")", restoredCells[1].GetProperty("userEnteredValue").GetProperty("formulaValue").GetString());
        Assert.Equal("=SUM(C3:C4)", restoredCells[2].GetProperty("userEnteredValue").GetProperty("formulaValue").GetString());
    }

    [Theory]
    [InlineData(SheetMutationKind.Insert, false)]
    [InlineData(SheetMutationKind.Delete, false)]
    [InlineData(SheetMutationKind.Insert, true)]
    [InlineData(SheetMutationKind.Delete, true)]
    public async Task Cross_tab_formula_or_named_range_rejects_before_destructive_batch(SheetMutationKind kind, bool namedRange)
    {
        var workbook = JsonNode.Parse(Sheet())!;
        if (namedRange)
            workbook["namedRanges"] = JsonNode.Parse("[{\"namedRangeId\":\"dependent\",\"name\":\"attendance\",\"range\":{\"sheetId\":7,\"startColumnIndex\":1,\"endColumnIndex\":2}}]");
        else workbook["sheets"]!.AsArray().Add(JsonNode.Parse("{\"properties\":{\"sheetId\":8,\"title\":\"summary\"},\"data\":[{\"rowData\":[{\"values\":[{\"userEnteredValue\":{\"formulaValue\":\"='2026-2'!B2\"}}]}]}]}"));
        await using var db = new TestDb(); using var transport = new Transport { Spreadsheet = workbook.ToJsonString() };
        var client = Client(db, transport); var before = await client.GetManagedTabAsync("synthetic", "2026-2");
        await Assert.ThrowsAsync<ConflictException>(() => client.ApplyManagedMutationAsync("synthetic", before, new("unsafe", kind, 2, "source", [])));
        Assert.Empty(transport.Posts);
    }

    [Fact]
    public async Task Formatted_cell_property_order_is_semantic_for_postimage_ownership()
    {
        await using var db = new TestDb(); using var transport = new Transport { Spreadsheet = Sheet() };
        var client = Client(db, transport); var before = await client.GetManagedTabAsync("synthetic", "2026-2");
        // Reorder the persisted preimage, not adapter output. The cell still owns identical note/format/value.
        var reordered = before.RawCellData!.Select(row => (IReadOnlyList<string>)row.Select(raw =>
        {
            var cell = JsonNode.Parse(raw)!.AsObject();
            return new JsonObject(cell.Reverse().Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone()))).ToJsonString();
        }).ToList()).ToList();
        Assert.NotEqual(before.RawCellData![0][1], reordered[0][1]);
        before = before with { RawCellData = reordered };
        var mutation = new ManagedSheetMutation("semantic", SheetMutationKind.Write, 2, "source", before.RawValues.Select(r => r.Count > 1 ? r[1] : "{}").ToList());
        transport.Spreadsheet = Sheet("semantic");
        var after = await client.GetManagedTabAsync("synthetic", "2026-2");
        await client.RestoreManagedTabAsync("synthetic", before, after, mutation);
        using var restored = JsonDocument.Parse(Assert.Single(transport.Posts));
        var cell = restored.RootElement.GetProperty("requests").EnumerateArray().Single(r => r.TryGetProperty("updateCells", out _))
            .GetProperty("updateCells").GetProperty("rows")[0].GetProperty("values")[1];
        Assert.Equal("keep note", cell.GetProperty("note").GetString());
        Assert.Equal(1, cell.GetProperty("userEnteredFormat").GetProperty("backgroundColor").GetProperty("red").GetDouble());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Divergent_value_or_format_postimage_never_restores_over_human_changes(bool format)
    {
        await using var db = new TestDb(); using var transport = new Transport { Spreadsheet = Sheet() };
        var client = Client(db, transport); var before = await client.GetManagedTabAsync("synthetic", "2026-2");
        var changed = JsonNode.Parse(Sheet("divergent"))!;
        var cell = changed["sheets"]![0]!["data"]![0]!["rowData"]![0]!["values"]![0]!;
        if (format) cell["userEnteredFormat"] = JsonNode.Parse("{\"backgroundColor\":{\"blue\":1}}");
        else cell["userEnteredValue"]!["stringValue"] = "human change";
        transport.Spreadsheet = changed.ToJsonString();
        var after = await client.GetManagedTabAsync("synthetic", "2026-2");
        await Assert.ThrowsAsync<ConflictException>(() => client.RestoreManagedTabAsync("synthetic", before, after,
            new("divergent", SheetMutationKind.Write, 2, "source", before.RawValues.Select(r => r.Count > 1 ? r[1] : "{}").ToList())));
        Assert.Empty(transport.Posts);
    }
}
