using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Google.Apis.Http;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Microsoft.Extensions.Configuration;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Application.Exceptions;
using VolleyballClub.Infrastructure.Services;

namespace VolleyballClub.Tests;

/// <summary>Runs the real Google adapter against an in-process HTTP handler; no credentials or network.</summary>
public sealed partial class GoogleManagedSheetTests
{
    private sealed class Transport : HttpMessageHandler, Google.Apis.Http.IHttpClientFactory
    {
        public string Spreadsheet { get; set; } = "";
        public List<string> Posts { get; } = [];
        public ConfigurableHttpClient CreateHttpClient(CreateHttpClientArgs args) => new(new ConfigurableMessageHandler(this));
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get)
                return new(HttpStatusCode.OK) { Content = new StringContent(Spreadsheet, Encoding.UTF8, "application/json") };
            Assert.Equal(HttpMethod.Post, request.Method);
            // 원본 메타데이터 병합 검색은 응용 계층 요청이다 — 기록하지 않고 빈 결과로 응답한다.
            if (request.RequestUri!.AbsolutePath.EndsWith("developerMetadata:search"))
                return new(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            Assert.EndsWith(":batchUpdate", request.RequestUri!.AbsolutePath);
            await using var content = await request.Content!.ReadAsStreamAsync(ct);
            await using var decoded = request.Content.Headers.ContentEncoding.Contains("gzip")
                ? (Stream)new System.IO.Compression.GZipStream(content, System.IO.Compression.CompressionMode.Decompress, true) : content;
            Posts.Add(await new StreamReader(decoded).ReadToEndAsync(ct));
            return new(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        }
    }
    private static string Sheet(string? marker = null, bool source = true) => """
        {"sheets":[{"properties":{"sheetId":7,"title":"2026-2","gridProperties":{"columnCount":3,"rowCount":10}},
        "data":[{"columnMetadata":[{"pixelSize":100},{"pixelSize":150,"hiddenByUser":true}],
        "rowData":[{"values":[{"userEnteredValue":{"stringValue":"부원"}},
          {"userEnteredValue":{"stringValue":"=literal"},"note":"keep note","userEnteredFormat":{"backgroundColor":{"red":1}}}]},
          {"values":[{}, {"userEnteredValue":{"formulaValue":"=SUM(B3:B4)"}}]}]}],
        "developerMetadata":[
        @@SOURCE@@
        @@COMMA@@
        @@MARKER@@
        ]}]}
        """.Replace("@@SOURCE@@", (source ? "{\"metadataId\":10,\"metadataKey\":\"vicikyu.activity-source\",\"metadataValue\":\"source\",\"visibility\":\"DOCUMENT\",\"location\":{\"dimensionRange\":{\"sheetId\":7,\"dimension\":\"COLUMNS\",\"startIndex\":1,\"endIndex\":2}}}" : "")).Replace("@@COMMA@@", (source && marker is not null ? "," : "")).Replace("@@MARKER@@", (marker is null ? "" : "{\"metadataId\":11,\"metadataKey\":\"vicikyu.last-edit\",\"metadataValue\":\"" + marker + "\",\"visibility\":\"DOCUMENT\",\"location\":{\"sheetId\":7}}"));
    private static GoogleSheetClient Client(TestDb db, Transport transport)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Google:ServiceAccountJson"] = "synthetic" }).Build();
        var client = new GoogleSheetClient(db.Factory, config);
        var service = new SheetsService(new BaseClientService.Initializer { HttpClientFactory = transport });
        typeof(GoogleSheetClient).GetField("service", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(client, service);
        typeof(GoogleSheetClient).GetField("serviceCredentials", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(client, "synthetic");
        return client;
    }

    [Theory]
    [InlineData(SheetMutationKind.Insert, "insertDimension")]
    [InlineData(SheetMutationKind.Write, "updateCells")]
    [InlineData(SheetMutationKind.Delete, "deleteDimension")]
    public async Task Structural_and_value_changes_share_one_atomic_batch(SheetMutationKind kind, string operation)
    {
        await using var db = new TestDb(); using var transport = new Transport { Spreadsheet = Sheet() };
        var client = Client(db, transport); var before = await client.GetManagedTabAsync("synthetic", "2026-2");
        Assert.Contains("stringValue", before.RawValues[0][1]); Assert.Contains("formulaValue", before.RawValues[1][1]);
        await client.ApplyManagedMutationAsync("synthetic", before, new("op", kind, 2, "source", ["{\"formulaValue\":\"=SUM(B2:B3)\"}"]));
        using var json = JsonDocument.Parse(Assert.Single(transport.Posts));
        var requests = json.RootElement.GetProperty("requests").EnumerateArray().ToList();
        Assert.Contains(requests, r => r.TryGetProperty(operation, out _));
        Assert.Contains(requests, r => r.TryGetProperty("createDeveloperMetadata", out var m) && m.GetProperty("developerMetadata").GetProperty("metadataValue").GetString() == "op");
        if (kind != SheetMutationKind.Delete)
            Assert.Equal("=SUM(B2:B3)", requests.Single(r => r.TryGetProperty("updateCells", out _)).GetProperty("updateCells")
                .GetProperty("rows")[0].GetProperty("values")[0].GetProperty("userEnteredValue").GetProperty("formulaValue").GetString());
    }

    [Fact]
    public async Task Legacy_one_marker_is_numeric_on_google_wire_without_coercing_other_strings()
    {
        var raw = typeof(GoogleSheetClient).Assembly.GetType("VolleyballClub.Infrastructure.Services.ManagedSheetEdits")!
            .GetMethod("Raw", BindingFlags.NonPublic | BindingFlags.Static)!;
        string Cell(string value) => (string)raw.Invoke(null, [value])!;
        await using var db = new TestDb(); using var transport = new Transport { Spreadsheet = Sheet() };
        var client = Client(db, transport); var before = await client.GetManagedTabAsync("synthetic", "2026-2");
        await client.ApplyManagedMutationAsync("synthetic", before, new("numeric", SheetMutationKind.Write, 2, "source",
            [Cell("26. 03. 01. 활동 완료"), Cell("1"), Cell("123"), Cell("출석")]));
        using var json = JsonDocument.Parse(Assert.Single(transport.Posts));
        var rows = json.RootElement.GetProperty("requests").EnumerateArray().Single(r => r.TryGetProperty("updateCells", out _))
            .GetProperty("updateCells").GetProperty("rows");
        var mark = rows[1].GetProperty("values")[0].GetProperty("userEnteredValue");
        Assert.Equal(1d, mark.GetProperty("numberValue").GetDouble()); Assert.False(mark.TryGetProperty("stringValue", out _));
        Assert.Equal("123", rows[2].GetProperty("values")[0].GetProperty("userEnteredValue").GetProperty("stringValue").GetString());
        Assert.Equal("출석", rows[3].GetProperty("values")[0].GetProperty("userEnteredValue").GetProperty("stringValue").GetString());
    }

    [Fact]
    public async Task Full_grid_append_is_atomic_and_restore_replays_typed_raw_cells_notes_format_and_column_properties()
    {
        await using var db = new TestDb(); using var transport = new Transport { Spreadsheet = Sheet() };
        var client = Client(db, transport); var before = await client.GetManagedTabAsync("synthetic", "2026-2");
        await client.ApplyManagedMutationAsync("synthetic", before, new("append", SheetMutationKind.Insert, 4, "new", []));
        Assert.Contains("appendDimension", Assert.Single(transport.Posts)); transport.Posts.Clear();
        var deleted = System.Text.Json.Nodes.JsonNode.Parse(Sheet("delete-op", false))!;
        var data = deleted["sheets"]![0]!["data"]![0]!;
        data["columnMetadata"]!.AsArray().RemoveAt(1);
        foreach (var row in data["rowData"]!.AsArray()) row!["values"]!.AsArray().RemoveAt(1);
        transport.Spreadsheet = deleted.ToJsonString();
        var after = await client.GetManagedTabAsync("synthetic", "2026-2");
        await client.RestoreManagedTabAsync("synthetic", before, after, new("delete-op", SheetMutationKind.Delete, 2, "source", []));
        using var json = JsonDocument.Parse(Assert.Single(transport.Posts));
        var requests = json.RootElement.GetProperty("requests").EnumerateArray().ToList();
        Assert.Contains(requests, r => r.TryGetProperty("insertDimension", out _));
        var restored = requests.Single(r => r.TryGetProperty("updateCells", out _)).GetProperty("updateCells").GetProperty("rows");
        Assert.Equal("=literal", restored[0].GetProperty("values")[1].GetProperty("userEnteredValue").GetProperty("stringValue").GetString());
        Assert.Equal("keep note", restored[0].GetProperty("values")[1].GetProperty("note").GetString());
        Assert.Equal("=SUM(B3:B4)", restored[1].GetProperty("values")[1].GetProperty("userEnteredValue").GetProperty("formulaValue").GetString());
        Assert.Contains(requests, r => r.TryGetProperty("updateDimensionProperties", out var p) && p.GetProperty("properties").GetProperty("pixelSize").GetInt32() == 150);
    }

    [Fact]
    public async Task Ambiguous_insert_recovery_never_deletes_the_following_column()
    {
        await using var db = new TestDb(); using var transport = new Transport { Spreadsheet = Sheet("op", false) };
        var client = Client(db, transport); var current = await client.GetManagedTabAsync("synthetic", "2026-2");
        await Assert.ThrowsAsync<ConflictException>(() => client.RestoreManagedTabAsync("synthetic", current, current,
            new("op", SheetMutationKind.Insert, 2, "missing-source-key", [])));
        Assert.Empty(transport.Posts);
    }
}
