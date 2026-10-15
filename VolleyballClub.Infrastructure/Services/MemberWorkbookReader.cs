using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using VolleyballClub.Application.Abstractions;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Application.Exceptions;

namespace VolleyballClub.Infrastructure.Services;

/// <summary>Only the supplied XLSX layout, not a general spreadsheet reader.</summary>
public sealed class MemberWorkbookReader : IMemberWorkbookReader
{
    private static readonly XNamespace Sheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string OfficeRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public IReadOnlyList<MemberCreateRequest> Read(Stream stream, string role, string password)
    {
        if (!MemberBulkValidation.ValidDefaults(role, password)) throw new RuleViolationException("기본 역할과 4자 이상의 초기 비밀번호를 먼저 설정해 주세요.");
        try
        {
            if (!stream.CanSeek || stream.Length > MemberBulkValidation.MaxFileBytes) throw Invalid();
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            if (zip.Entries.Count > 64 || zip.Entries.Sum(e => e.Length) > 8 * 1024 * 1024 ||
                zip.Entries.Any(e => e.Length > 2 * 1024 * 1024) ||
                zip.Entries.Select(e => e.FullName).Distinct(StringComparer.Ordinal).Count() != zip.Entries.Count) throw Invalid();

            XDocument Xml(string path)
            {
                var entry = zip.GetEntry(path) ?? throw Invalid();
                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                    MaxCharactersInDocument = 2 * 1024 * 1024, IgnoreComments = true
                };
                using (var input = entry.Open())
                using (var reader = XmlReader.Create(input, settings))
                {
                    var nodes = 0;
                    while (reader.Read())
                        if (reader.Depth > 32 || ++nodes > 30000) throw Invalid();
                }
                using var documentInput = entry.Open();
                using var documentReader = XmlReader.Create(documentInput, settings);
                return XDocument.Load(documentReader);
            }
            XNamespace content = "http://schemas.openxmlformats.org/package/2006/content-types";
            var types = Xml("[Content_Types].xml");
            if (types.Root?.Name != content + "Types" || !types.Root.Elements(content + "Override").Any(t =>
                (string?)t.Attribute("PartName") == "/xl/workbook.xml" &&
                (string?)t.Attribute("ContentType") == "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml")) throw Invalid();
            var package = Xml("_rels/.rels");
            if (package.Root?.Name != Rel + "Relationships" || !package.Root.Elements(Rel + "Relationship").Any(r =>
                (string?)r.Attribute("Type") == OfficeRel + "/officeDocument" &&
                (string?)r.Attribute("Target") is "xl/workbook.xml" or "/xl/workbook.xml" &&
                (string?)r.Attribute("TargetMode") != "External")) throw Invalid();
            var workbook = Xml("xl/workbook.xml");
            if (workbook.Root?.Name != Sheet + "workbook") throw Invalid();
            var sheets = workbook.Descendants(Sheet + "sheet").Where(s => (string?)s.Attribute("name") == "부원 목록").ToArray();
            if (sheets.Length != 1) throw Invalid();
            var id = (string?)sheets[0].Attribute(XName.Get("id", OfficeRel)) ?? throw Invalid();
            var relationships = Xml("xl/_rels/workbook.xml.rels").Root;
            if (relationships?.Name != Rel + "Relationships") throw Invalid();
            var relation = relationships.Elements(Rel + "Relationship").SingleOrDefault(r => (string?)r.Attribute("Id") == id) ?? throw Invalid();
            if ((string?)relation.Attribute("TargetMode") == "External" || (string?)relation.Attribute("Type") != OfficeRel + "/worksheet") throw Invalid();
            var target = (string?)relation.Attribute("Target") ?? throw Invalid();
            var sheetPath = target.StartsWith("/xl/", StringComparison.Ordinal) ? target[1..] : "xl/" + target;
            if (!sheetPath.StartsWith("xl/worksheets/", StringComparison.Ordinal) || sheetPath.Contains("..") || sheetPath.Contains('\\')) throw Invalid();
            var shared = zip.GetEntry("xl/sharedStrings.xml") is null ? null : Xml("xl/sharedStrings.xml");
            if (shared is not null && shared.Root?.Name != Sheet + "sst") throw Invalid();
            var strings = shared?.Root?.Elements(Sheet + "si")
                .Select(ReadString).ToArray() ?? [];
            if (strings.Length > 2000 || strings.Any(s => s.Length > 4096)) throw Invalid();
            var worksheet = Xml(sheetPath);
            if (worksheet.Root?.Name != Sheet + "worksheet") throw Invalid();
            var data = worksheet.Root.Elements(Sheet + "sheetData").SingleOrDefault() ?? throw Invalid();
            if (data.Elements().Count() > MemberBulkValidation.MaxRows + 1 ||
                data.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value))) throw Invalid();
            List<MemberCreateRequest> result = [];
            var previous = 0;
            foreach (var row in data.Elements())
            {
                if (row.Name != Sheet + "row" || !int.TryParse((string?)row.Attribute("r"), out var rowNumber) ||
                    rowNumber <= previous || rowNumber > MemberBulkValidation.MaxRows + 1 || row.Elements().Count() > 32 ||
                    row.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value))) throw Invalid();
                previous = rowNumber;
                var cells = new Dictionary<string, string>();
                foreach (var cell in row.Elements())
                {
                    if (cell.Name != Sheet + "c" || cell.Elements().Any(e => e.Name != Sheet + "v" && e.Name != Sheet + "is") ||
                        cell.Elements(Sheet + "v").Count() > 1 || cell.Elements(Sheet + "is").Count() > 1 ||
                        cell.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value))) throw Invalid();
                    var address = (string?)cell.Attribute("r") ?? throw Invalid();
                    var column = new string(address.TakeWhile(char.IsAsciiLetterUpper).ToArray());
                    if (column.Length is < 1 or > 3 || address != column + rowNumber || cells.ContainsKey(column)) throw Invalid();
                    var type = (string?)cell.Attribute("t");
                    if (type == "inlineStr" ? cell.Element(Sheet + "is") is null || cell.Element(Sheet + "v") is not null
                        : cell.Element(Sheet + "is") is not null) throw Invalid();
                    var scalar = cell.Element(Sheet + "v");
                    var value = scalar is null ? "" : Scalar(scalar);
                    if (type == "s")
                    {
                        if (!int.TryParse(value, out var index) || index < 0 || index >= strings.Length) throw Invalid();
                        value = strings[index];
                    }
                    else if (type == "inlineStr") value = ReadString(cell.Element(Sheet + "is")!);
                    else if (type is not (null or "n" or "str")) throw Invalid();
                    else if (column == "C" && rowNumber > 1 && value.Length > 0 && type is null or "n")
                    {
                        if (!decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || decimal.Truncate(number) != number) throw Invalid();
                        value = number.ToString("0", CultureInfo.InvariantCulture);
                    }
                    if (value.Length > 4096 || (column is not ("A" or "B" or "C") && !string.IsNullOrWhiteSpace(value))) throw Invalid();
                    cells.Add(column, value);
                }
                var name = cells.GetValueOrDefault("A", "");
                var department = cells.GetValueOrDefault("B", "");
                var student = cells.GetValueOrDefault("C", "");
                if (rowNumber == 1)
                {
                    if (name != "이름" || department != "학과" || student != "학번") throw Invalid();
                }
                else if (!string.IsNullOrWhiteSpace(name + department + student))
                {
                    result.Add(new(student, name, department, password, role));
                }
            }
            if (!data.Elements().Any(r => (string?)r.Attribute("r") == "1")) throw Invalid();
            return result;
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException or InvalidOperationException or IOException or OverflowException or ArgumentException)
        {
            throw Invalid();
        }
    }

    private static string Scalar(XElement value) => value.HasElements ? throw Invalid() : value.Value;

    private static readonly HashSet<XName> RunProperties = new[]
    {
        "rFont", "charset", "family", "b", "i", "strike", "outline", "shadow", "condense", "extend",
        "color", "sz", "u", "vertAlign", "scheme"
    }.Select(name => Sheet + name).ToHashSet();

    private static string ReadString(XElement value)
    {
        if (value.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value))) throw Invalid();
        var parts = value.Elements().ToArray();
        // Excel can append phonetic display metadata even when no reading text exists.
        if (parts.LastOrDefault()?.Name == Sheet + "phoneticPr")
        {
            if (parts[^1].HasElements || !string.IsNullOrWhiteSpace(parts[^1].Value)) throw Invalid();
            parts = parts[..^1];
        }
        if (parts.Length == 0) return "";
        if (parts.Length == 1 && parts[0].Name == Sheet + "t") return Scalar(parts[0]);
        if (parts.Any(p => p.Name != Sheet + "r")) throw Invalid();
        return string.Concat(parts.Select(run =>
        {
            var children = run.Elements().ToArray();
            if (run.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value)) ||
                children.Length is < 1 or > 2 || children[^1].Name != Sheet + "t") throw Invalid();
            if (children.Length == 2)
            {
                var properties = children[0];
                if (properties.Name != Sheet + "rPr" ||
                    properties.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value)) ||
                    properties.Elements().Any(p => !RunProperties.Contains(p.Name) || p.HasElements || !string.IsNullOrWhiteSpace(p.Value))) throw Invalid();
            }
            return Scalar(children[^1]);
        }));
    }

    private static RuleViolationException Invalid() => new("올바른 XLSX 부원 목록이 아닙니다. '부원 목록' 시트의 이름/학과/학번 열과 파일·행 제한(1MB, 500행)을 확인해 주세요. 수식은 지원하지 않습니다.");
}
