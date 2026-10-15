using System.IO.Compression;
using System.Text;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Application.Exceptions;
using VolleyballClub.Infrastructure.Services;

namespace VolleyballClub.Tests;

public sealed class MemberWorkbookTests
{
    internal static MemoryStream Workbook(string body, string sheetName = "부원 목록", string sharedStrings = "")
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            void Entry(string name, string text)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
                writer.Write(text);
            }
            Entry("[Content_Types].xml", """<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/></Types>""");
            Entry("_rels/.rels", """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="r1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
            Entry("xl/workbook.xml", $"""<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="{sheetName}" sheetId="1" r:id="rId1"/></sheets></workbook>""");
            Entry("xl/_rels/workbook.xml.rels", """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>""");
            Entry("xl/worksheets/sheet1.xml", """<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>이름</t></is></c><c r="B1" t="inlineStr"><is><t>학과</t></is></c><c r="C1" t="inlineStr"><is><t>학번</t></is></c></row>""" + body + "</sheetData></worksheet>");
            if (sharedStrings.Length > 0) Entry("xl/sharedStrings.xml", sharedStrings);
        }
        stream.Position = 0;
        return stream;
    }
    internal const string ValidRow = """<row r="2"><c r="A2" t="inlineStr"><is><t>테스트 부원</t></is></c><c r="C2"><v>209912345</v></c></row>""";

    [Fact]
    public void Numeric_And_Shared_String_Ids_Are_Strings_And_Defaults_Are_Snapshots()
    {
        using var file = Workbook(ValidRow.Replace("209912345", "2.09912345E8") + """<row r="3"><c r="A3" t="s"><v>0</v></c><c r="C3" t="s"><v>1</v></c></row>""", sharedStrings: """<sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><si><t>합성 부원</t></si><si><t>009912345</t></si></sst>""");
        var rows = new MemberWorkbookReader().Read(file, "ADMIN", "test-only");
        Assert.Equal("209912345", rows[0].StudentNumber);
        Assert.Equal("009912345", rows[1].StudentNumber);
        Assert.All(rows, r => { Assert.Equal("ADMIN", r.Role); Assert.Equal("test-only", r.Password); Assert.Equal("", r.Department); });
        Assert.Empty(MemberBulkValidation.Validate(rows));
    }

    [Theory]
    [InlineData("value")]
    [InlineData("inline")]
    [InlineData("shared")]
    [InlineData("text")]
    [InlineData("style")]
    public void Nested_Or_Unexpected_String_Elements_Are_Rejected(string kind)
    {
        var row = kind switch
        {
            "value" => ValidRow.Replace("<v>209912345</v>", "<v><t>209912345</t></v>"),
            "inline" => ValidRow.Replace("<t>테스트 부원</t>", "<t>테스트 부원</t><f>1+1</f>"),
            "shared" => ValidRow.Replace("<c r=\"A2\" t=\"inlineStr\"><is><t>테스트 부원</t></is></c>", "<c r=\"A2\" t=\"s\"><v>0</v></c>"),
            "text" => ValidRow.Replace("<t>테스트 부원</t>", "<t><b/>테스트 부원</t>"),
            _ => ValidRow.Replace("<t>테스트 부원</t>", "<r><rPr><f>1+1</f></rPr><t>테스트 부원</t></r>")
        };
        using var file = Workbook(row, sharedStrings: kind == "shared"
            ? """<sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><si><t>합성 부원</t><f>1+1</f></si></sst>""" : "");
        Assert.Throws<RuleViolationException>(() => new MemberWorkbookReader().Read(file, "MEMBER", "test-only"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Excel_Phonetic_Metadata_Is_Allowed_Only_When_Empty(bool malformed)
    {
        var metadata = malformed ? "<phoneticPr><f>1+1</f></phoneticPr>" : "<phoneticPr fontId=\"4\" type=\"noConversion\"/>";
        using var file = Workbook(
            ValidRow.Replace("<c r=\"A2\" t=\"inlineStr\"><is><t>테스트 부원</t></is></c>", "<c r=\"A2\" t=\"s\"><v>0</v></c>"),
            sharedStrings: "<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><si><t>합성 부원</t>" + metadata + "</si></sst>");
        if (malformed) Assert.Throws<RuleViolationException>(() => new MemberWorkbookReader().Read(file, "MEMBER", "test-only"));
        else Assert.Equal("합성 부원", Assert.Single(new MemberWorkbookReader().Read(file, "MEMBER", "test-only")).Name);
    }

    [Fact]
    public void Supported_Rich_Text_Runs_Preserve_Text()
    {
        using var file = Workbook(ValidRow.Replace("<t>테스트 부원</t>", "<r><rPr><b/></rPr><t>합성 </t></r><r><t>부원</t></r>"));
        Assert.Equal("합성 부원", Assert.Single(new MemberWorkbookReader().Read(file, "MEMBER", "test-only")).Name);
    }

    [Theory]
    [InlineData("formula")]
    [InlineData("error")]
    [InlineData("extra")]
    [InlineData("duplicate-cell")]
    [InlineData("missing-cell")]
    [InlineData("too-many-rows")]
    [InlineData("oversized-cell")]
    [InlineData("fraction")]
    [InlineData("wrong-sheet")]
    [InlineData("bad-header")]
    [InlineData("dtd")]
    public void Invalid_Structures_Are_Explicit_Errors(string kind)
    {
        var row = kind switch
        {
            "formula" => ValidRow.Replace("<v>209912345</v>", "<f>1+1</f><v>2</v>"),
            "error" => ValidRow.Replace("<c r=\"C2\">", "<c r=\"C2\" t=\"e\">"),
            "extra" => ValidRow.Replace("</row>", "<c r=\"D2\"><v>9</v></c></row>"),
            "duplicate-cell" => ValidRow.Replace("</row>", "<c r=\"C2\"><v>9</v></c></row>"),
            "missing-cell" => ValidRow.Replace("r=\"C2\"", "r=\"C3\""),
            "too-many-rows" => string.Concat(Enumerable.Range(2, 501).Select(i => $"<row r=\"{i}\"/>")),
            "oversized-cell" => ValidRow.Replace("테스트 부원", new string('x', 4097)),
            "fraction" => ValidRow.Replace("209912345", "123.5"),
            _ => ValidRow
        };
        using var file = Workbook(row, kind == "wrong-sheet" ? "other" : "부원 목록");
        if (kind is "bad-header" or "dtd")
        {
            using var zip = new ZipArchive(file, ZipArchiveMode.Update, true);
            var entry = zip.GetEntry("xl/worksheets/sheet1.xml")!;
            string xml;
            using (var reader = new StreamReader(entry.Open())) xml = reader.ReadToEnd();
            entry.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("xl/worksheets/sheet1.xml").Open());
            writer.Write(kind == "bad-header" ? xml.Replace("이름", "wrong") : "<!DOCTYPE worksheet [<!ENTITY x 'bad'>]>" + xml);
        }
        file.Position = 0;
        Assert.Throws<RuleViolationException>(() => new MemberWorkbookReader().Read(file, "MEMBER", "test-only"));
    }

    [Theory]
    [InlineData("file-size")]
    [InlineData("zip-entries")]
    [InlineData("zip-expansion")]
    [InlineData("duplicate-entry")]
    [InlineData("xml-depth")]
    [InlineData("cell-count")]
    [InlineData("package-metadata")]
    public void Untrusted_Resource_Bounds_Are_Enforced(string kind)
    {
        using var file = Workbook(kind == "cell-count" ? "<row r=\"2\">" + string.Concat(Enumerable.Repeat("<c r=\"A2\"/>", 33)) + "</row>" : ValidRow);
        if (kind == "file-size") file.SetLength(MemberBulkValidation.MaxFileBytes + 1);
        else if (kind != "cell-count")
        {
            using var zip = new ZipArchive(file, ZipArchiveMode.Update, true);
            if (kind == "zip-entries")
                for (var i = 0; i < 65; i++) zip.CreateEntry($"extra-{i}");
            else if (kind == "duplicate-entry") zip.CreateEntry("xl/workbook.xml");
            else if (kind == "package-metadata") zip.GetEntry("[Content_Types].xml")!.Delete();
            else if (kind == "zip-expansion")
            {
                for (var i = 0; i < 9; i++)
                {
                    using var output = zip.CreateEntry($"expanded-{i}").Open();
                    output.Write(new byte[1024 * 1024]);
                }
            }
            else
            {
                zip.GetEntry("xl/worksheets/sheet1.xml")!.Delete();
                using var writer = new StreamWriter(zip.CreateEntry("xl/worksheets/sheet1.xml").Open());
                writer.Write(string.Concat(Enumerable.Repeat("<x>", 40)) + string.Concat(Enumerable.Repeat("</x>", 40)));
            }
        }
        file.Position = 0;
        Assert.Throws<RuleViolationException>(() => new MemberWorkbookReader().Read(file, "MEMBER", "test-only"));
    }

    [Fact]
    public void Empty_Template_And_Populated_Invalid_Row_Are_Not_Confused()
    {
        using var empty = Workbook("");
        Assert.Empty(new MemberWorkbookReader().Read(empty, "MEMBER", "test-only"));
        using var partial = Workbook("""<row r="2"><c r="B2" t="inlineStr"><is><t>선택 학과</t></is></c></row>""");
        var rows = new MemberWorkbookReader().Read(partial, "MEMBER", "test-only");
        Assert.Single(rows);
        Assert.Contains(MemberBulkValidation.Validate(rows), e => e.Row == 1);
        Assert.Throws<RuleViolationException>(() => new MemberWorkbookReader().Read(empty, "ROOT", "test-only"));
        Assert.Throws<RuleViolationException>(() => new MemberWorkbookReader().Read(empty, "MEMBER", ""));
        Assert.False(MemberBulkValidation.ValidDefaults("MEMBER", "    "));
        Assert.Throws<RuleViolationException>(() => new MemberWorkbookReader().Read(empty, "MEMBER", "    "));
    }
}
