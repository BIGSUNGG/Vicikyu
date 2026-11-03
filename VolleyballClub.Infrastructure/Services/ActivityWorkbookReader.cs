using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Application.Exceptions;

namespace VolleyballClub.Infrastructure.Services;

/// <summary>활동 기록 xlsx의 모든 탭을 원시 그리드로 읽는다(부원 목록 리더와 같은 안전 규칙, 활동 기록 규모에 맞는 상한).
/// 수식은 캐시 값만 읽고, 서버에 파일을 저장하지 않는다(스트림에서 곧바로 파싱·폐기).</summary>
public sealed partial class ActivityWorkbookReader
{
    private static readonly XNamespace Sheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string OfficeRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public const int MaxFileBytes = 8 * 1024 * 1024;

    public IReadOnlyList<TabGrid> Read(Stream stream)
    {
        try
        {
            if (!stream.CanSeek || stream.Length > MaxFileBytes) throw Invalid();
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            if (zip.Entries.Count > 128 || zip.Entries.Sum(e => e.Length) > 32 * 1024 * 1024 ||
                zip.Entries.Any(e => e.Length > 8 * 1024 * 1024) ||
                zip.Entries.Select(e => e.FullName).Distinct(StringComparer.Ordinal).Count() != zip.Entries.Count) throw Invalid();

            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = 32 * 1024 * 1024, IgnoreComments = true
            };
            XDocument Xml(string path)
            {
                var entry = zip.GetEntry(path) ?? throw Invalid();
                using (var input = entry.Open())
                using (var probe = XmlReader.Create(input, settings))
                {
                    // 트리 폭탄·거대 DOM 방지 — 파싱 전에 깊이·노드 수 상한으로 조기 거부(부원 리더와 같은 규칙, 활동 규모 상한)
                    var nodes = 0;
                    while (probe.Read())
                        if (probe.Depth > 48 || ++nodes > 500_000) throw Invalid();
                }
                using var documentInput = entry.Open();
                using var documentReader = XmlReader.Create(documentInput, settings);
                return XDocument.Load(documentReader);
            }

            var workbook = Xml("xl/workbook.xml");
            if (workbook.Root?.Name != Sheet + "workbook") throw Invalid();
            var relationships = Xml("xl/_rels/workbook.xml.rels").Root;
            if (relationships?.Name != Rel + "Relationships") throw Invalid();
            var relMap = relationships.Elements(Rel + "Relationship")
                .Where(r => (string?)r.Attribute("Type") == OfficeRel + "/worksheet" && (string?)r.Attribute("TargetMode") != "External")
                .ToDictionary(r => (string?)r.Attribute("Id") ?? "", r => (string?)r.Attribute("Target") ?? "");
            var shared = zip.GetEntry("xl/sharedStrings.xml") is null ? null : Xml("xl/sharedStrings.xml");
            var strings = shared?.Root?.Elements(Sheet + "si").Select(ReadString).ToArray() ?? [];
            if (strings.Length > 50_000 || strings.Any(x => x.Length > 8192)) throw Invalid();

            var tabs = new List<TabGrid>();
            foreach (var sheetElement in workbook.Descendants(Sheet + "sheet"))
            {
                var name = ((string?)sheetElement.Attribute("name") ?? "").Trim();
                if (name.Length == 0 || name.Length > 100) throw Invalid();
                var id = (string?)sheetElement.Attribute(XName.Get("id", OfficeRel)) ?? throw Invalid();
                if (!relMap.TryGetValue(id, out var target)) throw Invalid();
                var sheetPath = target.StartsWith("/xl/", StringComparison.Ordinal) ? target[1..] : "xl/" + target;
                if (!sheetPath.StartsWith("xl/worksheets/", StringComparison.Ordinal) || sheetPath.Contains("..") || sheetPath.Contains('\\')) throw Invalid();

                var worksheet = Xml(sheetPath);
                if (worksheet.Root?.Name != Sheet + "worksheet") throw Invalid();
                var data = worksheet.Root.Elements(Sheet + "sheetData").SingleOrDefault() ?? throw Invalid();
                var rows = new List<IReadOnlyList<string>>();
                var previous = 0;
                foreach (var rowElement in data.Elements(Sheet + "row"))
                {
                    if (!int.TryParse((string?)rowElement.Attribute("r"), out var rowNumber) ||
                        rowNumber <= previous || rowNumber > 2000 || rowElement.Elements().Count() > 128) throw Invalid();
                    // 빈 행은 빈 칸 행으로 채운다(원본 그리드의 행 정렬 유지)
                    while (rows.Count < rowNumber - 1) rows.Add([]);
                    previous = rowNumber;
                    var cellCount = rowElement.Elements(Sheet + "c")
                        .Select(c => ColumnNumber((string?)c.Attribute("r") ?? "")).DefaultIfEmpty(0).Max();
                    var cells = new string[cellCount];
                    for (var i = 0; i < cellCount; i++) cells[i] = "";
                    foreach (var cell in rowElement.Elements(Sheet + "c"))
                    {
                        var address = (string?)cell.Attribute("r") ?? throw Invalid();
                        var index = ColumnNumber(address) - 1;
                        if (index < 0) throw Invalid();
                        cells[index] = ReadCell(cell, strings);
                    }
                    rows.Add(cells);
                }

                tabs.Add(new TabGrid(name, rows));
            }

            if (tabs.Count == 0 || tabs.Count > 32) throw Invalid();
            return tabs;
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException or InvalidOperationException or IOException or OverflowException or ArgumentException)
        {
            throw Invalid();
        }
    }

    private static string ReadCell(XElement cell, string[] strings)
    {
        var type = (string?)cell.Attribute("t");
        var formula = cell.Element(Sheet + "f");
        if (type == "inlineStr")
        {
            return cell.Element(Sheet + "is") is { } inline ? ReadString(inline) : "";
        }
        var scalar = cell.Element(Sheet + "v");
        var value = scalar is null ? "" : (scalar.HasElements ? throw Invalid() : scalar.Value);
        if (type == "s")
        {
            if (!int.TryParse(value, out var index) || index < 0 || index >= strings.Length) throw Invalid();
            return strings[index];
        }
        if (type is not (null or "n" or "str")) return "";
        // 수식 캐시 값은 그대로 두고, 없으면 빈 칸으로 취급한다(집계 칼럼 등 — 파서가 날짜 헤더가 아니면 건너뛴다)
        if (formula is not null && scalar is null) return "";
        if (value.Length == 0) return "";
        // 숫자 값 정규화 — 정수는 '1', 실수(일련번호·1.0)는 불변 문화권 그대로
        if (type is null or "n" && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return decimal.Truncate((decimal)number) == (decimal)number
                ? decimal.ToInt64((decimal)number).ToString(CultureInfo.InvariantCulture)
                : value;
        }
        return value;
    }

    [GeneratedRegex(@"^[A-Z]{1,3}")]
    private static partial Regex ColumnRegex();

    private static int ColumnNumber(string address)
    {
        var match = ColumnRegex().Match(address);
        if (!match.Success) return 0;
        var n = 0;
        foreach (var ch in match.Value) n = n * 26 + (ch - 'A' + 1);
        return n;
    }

    private static string ReadString(XElement value)
    {
        var parts = value.Elements().ToArray();
        if (parts.Length == 0) return "";
        if (parts.Length == 1 && parts[0].Name == Sheet + "t") return parts[0].Value;
        // 서식 런(r) 조합과 포그래픽 메타데이터만 허용한다
        return string.Concat(parts.Where(p => p.Name == Sheet + "r").SelectMany(run =>
            run.Elements(Sheet + "t").Select(t => t.Value)));
    }

    private static RuleViolationException Invalid() => new("올바른 XLSX 활동 기록 파일이 아닙니다. 학기별 탭 출석부 파일과 크기 제한(8MB)을 확인해 주세요.");
}
