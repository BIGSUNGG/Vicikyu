using System.IO.Compression;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Application.Exceptions;
using VolleyballClub.Domain.Entities;
using VolleyballClub.Domain.Enums;
using VolleyballClub.Infrastructure.Services;

namespace VolleyballClub.Tests;

/// <summary>테스트용 최소 xlsx 작성기 — 인라인 문자열과 숫자 셀만 만든다(서식·수식 없음).
/// 프로덕션 ActivityWorkbookWriter와 같은 OOXML 구조를 테스트에서 독립적으로 재현한다.</summary>
internal static class TestWorkbook
{
    public static byte[] Write(params (string Name, string[][] Grid)[] tabs)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            void Entry(string name, string text)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
                writer.Write(text);
            }

            var contentTypes = new StringBuilder("""<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="xml" ContentType="application/xml"/><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>""");
            for (var i = 1; i <= tabs.Length; i++)
                contentTypes.Append($"""<Override PartName="/xl/worksheets/sheet{i}.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>""");
            contentTypes.Append("</Types>");
            Entry("[Content_Types].xml", contentTypes.ToString());

            Entry("_rels/.rels", """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");

            var workbook = new StringBuilder("""<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets>""");
            for (var i = 1; i <= tabs.Length; i++)
                workbook.Append($"""<sheet name="{tabs[i - 1].Name}" sheetId="{i}" r:id="rId{i}"/>""");
            workbook.Append("</sheets></workbook>");
            Entry("xl/workbook.xml", workbook.ToString());

            var rels = new StringBuilder("""<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">""");
            for (var i = 1; i <= tabs.Length; i++)
                rels.Append($"""<Relationship Id="rId{i}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet{i}.xml"/>""");
            rels.Append("</Relationships>");
            Entry("xl/_rels/workbook.xml.rels", rels.ToString());

            for (var t = 0; t < tabs.Length; t++)
            {
                var sheet = new StringBuilder("""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>""");
                for (var r = 0; r < tabs[t].Grid.Length; r++)
                {
                    sheet.Append($"""<row r="{r + 1}">""");
                    for (var c = 0; c < tabs[t].Grid[r].Length; c++)
                    {
                        var value = tabs[t].Grid[r][c];
                        if (value.Length == 0) continue;
                        var address = $"{ColumnName(c + 1)}{r + 1}";
                        if (IsInteger(value)) sheet.Append($"""<c r="{address}"><v>{value}</v></c>""");
                        else sheet.Append($"""<c r="{address}" t="inlineStr"><is><t>{Escape(value)}</t></is></c>""");
                    }
                    sheet.Append("</row>");
                }
                sheet.Append("</sheetData></worksheet>");
                Entry($"xl/worksheets/sheet{t + 1}.xml", sheet.ToString());
            }
        }
        return stream.ToArray();
    }

    private static bool IsInteger(string value) => value.Length is >= 1 and <= 15 && value.All(char.IsAsciiDigit);

    private static string Escape(string value) => value
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string ColumnName(int column)
    {
        var letters = "";
        while (column > 0)
        {
            letters = (char)('A' + (column - 1) % 26) + letters;
            column = (column - 1) / 26;
        }
        return letters;
    }
}

/// <summary>활동 기록 xlsx 파서·가져오기·내보내기 계약 — 서식 자동 감지, 멱등 병합, 미매핑 보관·연동, 왕복 불변.</summary>
public sealed class ActivityArchiveTests
{
    // ===== 파서(서식 감지·헤더 해석) =====

    [Fact]
    public void 구형_탭은_1과_O_마크를_출석으로_읽고_빈_날짜는_예정으로_둔다()
    {
        var data = SemesterTabParser.Parse(new("2023-2",
        [
            ["부원", "23. 03. 03. 금", "23. 03. 10. 금", "23. 03. 17. 금"],
            ["김민수 24 체육", "1", "O", ""],
            ["박지훈 23 체육", "O", "", ""],
        ]));

        Assert.Equal(3, data.Activities.Count);
        Assert.Equal(new DateOnly(2023, 3, 3), data.Activities[0].Date);
        Assert.Equal(DayKind.Completed, data.Activities[0].Kind);
        Assert.Equal(["김민수 24 체육", "박지훈 23 체육"], data.Activities[0].AttendedRowLabels);
        Assert.Equal(["김민수 24 체육"], data.Activities[1].AttendedRowLabels);
        Assert.Equal(new DateOnly(2023, 3, 17), data.Activities[2].Date);
        Assert.Equal(DayKind.Scheduled, data.Activities[2].Kind); // 미표시 빈 날짜는 예정
        Assert.Empty(data.Activities[2].AttendedRowLabels);
    }

    [Fact]
    public void O_X_표기_탭은_O만_출석으로_읽는다()
    {
        var data = SemesterTabParser.Parse(new("2023-1",
        [
            ["부원", "23. 03. 03. 금"],
            ["김민수", "O"],
            ["박지훈", "X"],
        ]));

        var column = Assert.Single(data.Activities);
        Assert.Equal(DayKind.Completed, column.Kind);
        Assert.Equal(["김민수"], column.AttendedRowLabels);
    }

    [Fact]
    public void 신형_탭은_상태행을_읽고_특수_집계_칼럼을_건너뛴다()
    {
        var data = SemesterTabParser.Parse(new("2026-2",
        [
            ["활동 상태", "활동 완료", "", "", "", "활동 예정", "활동 없음(시험)", ""],
            ["총 인원 : ", "26. 09. 01. 화 OT", "OT 참여", "세미나(출석 -1)", "회비 납부 O / X", "26. 09. 08. 화", "26. 09. 15. 화", "9월 출석횟수"],
            ["김민수 24 체육", "출석", "1", "1", "O", "", "", "1"],
            ["총 참여자 수", "=COUNTIF(B3:B3,\"출석\")", "", "", "", "", "", ""],
        ]));

        Assert.Equal(3, data.Activities.Count); // 날짜가 아닌 헤더(OT 참여·세미나·회비·집계)는 건너뛴다
        var completed = data.Activities[0];
        Assert.Equal(new DateOnly(2026, 9, 1), completed.Date);
        Assert.Equal(DayKind.Completed, completed.Kind);
        Assert.Equal("OT", completed.Label);
        Assert.Equal(["김민수 24 체육"], completed.AttendedRowLabels);
        Assert.Equal(new DateOnly(2026, 9, 8), data.Activities[1].Date);
        Assert.Equal(DayKind.Scheduled, data.Activities[1].Kind);
        Assert.Empty(data.Activities[1].AttendedRowLabels);
        var none = data.Activities[2];
        Assert.Equal(new DateOnly(2026, 9, 15), none.Date);
        Assert.Equal(DayKind.NoActivity, none.Kind);
        Assert.Equal("시험", none.Reason);
    }

    [Fact]
    public void 엑셀_일련번호_날짜_헤더를_해석한다()
    {
        Assert.Equal(new DateOnly(2026, 9, 4), SemesterTabParser.TryParseHeaderDate("26. 09. 04. 금"));
        Assert.Equal(new DateOnly(2026, 12, 4), SemesterTabParser.TryParseHeaderDate("46360"));
        Assert.Null(SemesterTabParser.TryParseHeaderDate("OT 참여"));
        Assert.Null(SemesterTabParser.TryParseHeaderDate("9월 출석횟수"));
        Assert.Null(SemesterTabParser.TryParseHeaderDate("70000")); // 일련번호 범위 밖

        var data = SemesterTabParser.Parse(new("2026-2",
        [
            ["활동 상태", "활동 예정"],
            ["총 인원 : ", "46360"],
        ]));
        var column = Assert.Single(data.Activities);
        Assert.Equal(new DateOnly(2026, 12, 4), column.Date);
        Assert.Equal(DayKind.Scheduled, column.Kind);
    }

    [Fact]
    public void 라벨_접미와_없음_사유를_정확히_뽑는다()
    {
        Assert.Equal("OT", SemesterTabParser.LabelOf("26. 09. 04. 금 OT"));
        Assert.Null(SemesterTabParser.LabelOf("26. 09. 11. 금"));
        Assert.Equal("시험", SemesterTabParser.ExtractReason("활동 없음(시험)"));
        Assert.Null(SemesterTabParser.ExtractReason("활동 완료"));
        Assert.Null(SemesterTabParser.ExtractReason("활동 없음()"));
    }

    // ===== 가져오기 병합·멱등 =====

    private static byte[] ImportWorkbook() => TestWorkbook.Write(("2026-2",
    [
        ["활동 상태", "활동 완료", "활동 완료"],
        ["총 인원 : ", "26. 09. 01. 화", "26. 09. 08. 화"],
        ["김민수 2025 체육", "출석", ""],
        ["외부인 99", "", "출석"],
    ]));

    [Fact]
    public async Task 가져오기는_멱등이고_재업로드는_중복을_만들지_않는다()
    {
        await using var host = new ServiceHost();
        host.AsAdmin();
        await host.SeedUserAsync("김민수", "2025");

        var report = await ImportAsync(host, ImportWorkbook());
        Assert.Equal(2, report.ImportedActivities);
        Assert.Equal(1, report.ImportedAttendances);
        Assert.Equal(0, report.DayMarks);
        Assert.Equal(1, report.UnmappedMembers);
        Assert.Equal(1, report.UnmappedAttendances);
        Assert.Contains(report.Warnings, w => w.Contains("매칭 실패"));

        // 같은 파일 재업로드 — 모두 0, 어떤 행도 중복되지 않는다
        var retry = await ImportAsync(host, ImportWorkbook());
        Assert.Equal(0, retry.ImportedActivities);
        Assert.Equal(0, retry.ImportedAttendances);
        Assert.Equal(0, retry.DayMarks);
        Assert.Equal(0, retry.UnmappedMembers);
        Assert.Equal(0, retry.UnmappedAttendances);

        await using var db = host.Db.CreateContext();
        Assert.Equal(2, await db.Activities.CountAsync());
        Assert.Equal(2, await db.SemesterAttendances.CountAsync()); // 김민수 1 + 외부인 1
        var rows = await db.SemesterMembers.ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.MappedUserId != null && r.Label == "김민수 2025 체육"); // 자동 매핑
        Assert.Contains(rows, r => r.MappedUserId == null && r.Label == "외부인 99"); // 미매핑 영구 보관
    }

    [Fact]
    public async Task 미매핑_행은_매핑후에도_삭제되지_않고_출석이_계정_통계에_합산된다()
    {
        await using var host = new ServiceHost();
        host.AsAdmin();
        var member = await host.SeedUserAsync("김민수", "2025");
        // 외부인 행은 김민수와 다른 학기 탭에 둔다 — 탭명 학기가 자동 생성되어
        // '같은 학기 한 계정 한 행' 규칙이 적용되기 때문이다.
        await ImportAsync(host, TestWorkbook.Write(
            ("2026-2",
            [
                ["활동 상태", "활동 완료"],
                ["총 인원 : ", "26. 09. 01. 화"],
                ["김민수 2025 체육", "출석"],
            ]),
            ("2025-1",
            [
                ["활동 상태", "활동 완료"],
                ["총 인원 : ", "25. 03. 08. 토"],
                ["외부인 99", "출석"],
            ])));

        var list = await host.SheetRows.GetListAsync(mapped: false);
        var unmapped = Assert.Single(list);
        Assert.Equal("외부인 99", unmapped.Label);
        Assert.Equal("외부인", unmapped.Name);
        Assert.Equal(1, unmapped.AttendanceCount);

        await host.SheetRows.SetMappingAsync(unmapped.Id, member);

        await using var db = host.Db.CreateContext();
        var row = await db.SemesterMembers.SingleAsync(m => m.Id == unmapped.Id); // 매핑후에도 행 유지(영구)
        Assert.Equal(member, row.MappedUserId);
        // 가져오기 출석 1건(9/1, 자동 매핑된 김민수 행) + 매핑된 외부인 행 1건(9/8) = 조인 집계 2건
        var attendances = await db.SemesterAttendances
            .Where(a => a.SemesterMember.MappedUserId == member).ToListAsync();
        Assert.Equal(2, attendances.Count);
        Assert.All(attendances, a => Assert.Equal(AttendanceType.Import, a.AttendanceType));

        // 없는 행 매핑은 NotFound다
        await Assert.ThrowsAsync<NotFoundException>(() => host.SheetRows.SetMappingAsync(999, member));
    }

    [Fact]
    public async Task 같은_미매핑_라벨이_여러_칼럼에_있어도_한_명으로_묶인다()
    {
        await using var host = new ServiceHost();
        host.AsAdmin();
        await host.SeedUserAsync("김민수", "2025");

        var report = await ImportAsync(host, TestWorkbook.Write(("2023-1",
        [
            ["총 인원 : ", "23. 03. 10. 금", "23. 03. 17. 금", "23. 03. 24. 금"],
            ["김민수 2025 체육", "O", "O", ""],
            ["졸업생", "O", "", "O"],
        ])));

        Assert.Equal(3, report.ImportedActivities);
        Assert.Equal(2, report.ImportedAttendances); // 김민수 3/10·3/17
        Assert.Equal(1, report.UnmappedMembers); // 여러 칼럼에 흩어져도 미매핑 부원은 1행
        Assert.Equal(2, report.UnmappedAttendances); // 졸업생 3/10·3/24

        await using var db = host.Db.CreateContext();
        Assert.Equal(1, await db.SemesterMembers.CountAsync(m => m.MappedUserId == null));
        Assert.Equal(2, await db.SemesterAttendances.CountAsync(a => a.SemesterMember.MappedUserId == null));
    }

    [Fact]
    public async Task 한_파일_안에서_완료_칼럼이_같은_날짜의_예정_마크를_대체한다()
    {
        await using var host = new ServiceHost();
        host.AsAdmin();
        await host.SeedUserAsync("김민수", "2025");

        // 탭 A: 10.01 예정 → 탭 B: 같은 날 완료 — 마크는 활동으로 대체되고 공존하지 않는다
        var report = await ImportAsync(host, TestWorkbook.Write(
            ("2026-2A",
            [
                ["활동 상태", "활동 예정"],
                ["총 인원 : ", "26. 10. 01. 목"],
            ]),
            ("2026-2B",
            [
                ["활동 상태", "활동 완료"],
                ["총 인원 : ", "26. 10. 01. 목"],
                ["김민수 2025 체육", "출석"],
            ])));

        Assert.Equal(1, report.ImportedActivities);

        await using var db = host.Db.CreateContext();
        Assert.Empty(await db.DayMarks.ToListAsync());
        var activity = Assert.Single(await db.Activities.ToListAsync());
        Assert.Equal(new DateOnly(2026, 10, 1), activity.ActivityDate);
    }

    [Fact]
    public async Task 같은_날짜_라벨이_두_탭에_겹쳐도_활동은_하나다()
    {
        await using var host = new ServiceHost();
        host.AsAdmin();
        await host.SeedUserAsync("김민수", "2025");

        var report = await ImportAsync(host, TestWorkbook.Write(
            ("2026-2 사본",
            [
                ["활동 상태", "활동 완료"],
                ["총 인원 : ", "26. 09. 01. 화"],
                ["김민수 2025 체육", "출석"],
            ]),
            ("2026-2 원본",
            [
                ["활동 상태", "활동 완료"],
                ["총 인원 : ", "26. 09. 01. 화"],
                ["김민수 2025 체육", "출석"],
            ])));

        Assert.Equal(1, report.ImportedActivities);

        await using var db = host.Db.CreateContext();
        var activity = Assert.Single(await db.Activities.ToListAsync());
        Assert.Equal(1, await db.SemesterAttendances.CountAsync(a => a.ActivityId == activity.Id));
    }

    private static async Task<ActivityImportReport> ImportAsync(ServiceHost host, byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        return await host.Archive.ImportAsync(stream);
    }

    // ===== 내보내기 왕복 불변 =====

    [Fact]
    public async Task 내보내기_가져오기_왕복은_학기_기록을_동등하게_보존한다()
    {
        await using var source = new ServiceHost();
        var admin = await source.SeedUserAsync("김회장", "admin");
        var kim = await source.SeedUserAsync("김민수", "2025");
        var park = await source.SeedUserAsync("박지훈", "2026");
        source.AsAdmin(admin);
        await source.Semester.CreateAsync(new SemesterSaveRequest("2026-2", new(2026, 9, 1), new(2026, 12, 31)));

        var regular = await source.AdminActivities.CreateAsync(new ActivityCreateRequest(new(2026, 9, 1), DayKind.Completed));
        var ot = await source.AdminActivities.CreateAsync(new ActivityCreateRequest(new(2026, 9, 4), DayKind.Completed, Label: "OT"));
        await source.AdminActivities.CreateAsync(new ActivityCreateRequest(new(2026, 9, 15), DayKind.NoActivity, Note: "추석 연휴"));
        await source.AdminActivities.CreateAsync(new ActivityCreateRequest(new(2026, 9, 22), DayKind.Scheduled));
        await source.AdminActivities.SetAttendanceAsync(regular.Key!, kim, new ActivityAttendanceRequest(true));
        await source.AdminActivities.SetAttendanceAsync(regular.Key!, park, new ActivityAttendanceRequest(true));
        await source.AdminActivities.SetAttendanceAsync(ot.Key!, park, new ActivityAttendanceRequest(true));

        var bytes = await source.Archive.ExportAsync();

        // 내보낸 파일은 학기 탭 하나로 파싱된다
        using (var read = new MemoryStream(bytes))
        {
            var tab = Assert.Single(new ActivityWorkbookReader().Read(read));
            Assert.Equal("2026-2", tab.Name);
        }

        // 같은 부원이 있는 새 DB로 가져오면 기록이 동등하게 재현된다
        await using var target = new ServiceHost();
        await target.SeedUserAsync("김민수", "2025");
        await target.SeedUserAsync("박지훈", "2026");
        target.AsAdmin();
        var report = await ImportAsync(target, bytes);
        Assert.Equal(2, report.ImportedActivities);
        Assert.Equal(3, report.ImportedAttendances);
        Assert.Equal(2, report.DayMarks);
        Assert.Empty(report.Warnings);

        await using (var db = target.Db.CreateContext())
        {
            var activities = await db.Activities.AsNoTracking().OrderBy(a => a.ActivityDate).ToListAsync();
            Assert.Equal(2, activities.Count);
            Assert.All(activities, a =>
            {
                Assert.Equal(ActivityStatus.ActivityClosed, a.Status);
                Assert.Equal(Activity.ImportCreator, a.CreatedByUserId);
            });
            Assert.Null(activities[0].DisplayName); // 정기 활동
            Assert.Equal("OT", activities[1].DisplayName); // 라벨 활동

            var pairs = await db.SemesterAttendances.AsNoTracking()
                .Select(a => new { a.Activity!.ActivityDate, a.Activity.DisplayName, UserId = a.SemesterMember.MappedUserId })
                .OrderBy(x => x.ActivityDate).ThenBy(x => x.UserId).ToListAsync();
            Assert.Equal(new[]
            {
                new { ActivityDate = new DateOnly(2026, 9, 1), DisplayName = (string?)null, UserId = kim },
                new { ActivityDate = new DateOnly(2026, 9, 1), DisplayName = (string?)null, UserId = park },
                new { ActivityDate = new DateOnly(2026, 9, 4), DisplayName = (string?)"OT", UserId = park },
            }.OrderBy(x => x.ActivityDate).ThenBy(x => x.UserId).ToList(), pairs);

            var marks = await db.DayMarks.AsNoTracking().OrderBy(m => m.Date).ToListAsync();
            Assert.Equal(2, marks.Count);
            Assert.Equal(new DateOnly(2026, 9, 15), marks[0].Date);
            Assert.Equal(DayKind.NoActivity, marks[0].Kind);
            Assert.Equal("추석 연휴", marks[0].Note);
            Assert.Equal(new DateOnly(2026, 9, 22), marks[1].Date);
            Assert.Equal(DayKind.Scheduled, marks[1].Kind);
        }

        // 왕복 2회차(가져온 DB를 다시 내보내·가져오기) — 과거 '예정' 마크는 가져오기 시작 스윕이 '없음'으로 전환한 뒤
        // 다시 '예정'으로 쓰므로 1건이 갱신되고, 이후 조회 지점 스윕이 다시 '없음'으로 되돌려 상태가 수렴한다
        var reExport = await target.Archive.ExportAsync();
        var reImport = await ImportAsync(target, reExport);
        Assert.Equal(0, reImport.ImportedActivities);
        Assert.Equal(0, reImport.ImportedAttendances);
        Assert.Equal(1, reImport.DayMarks);
        await target.AdminActivities.GetActivitiesAsync();
        await using var converged = target.Db.CreateContext();
        var convergedMark = await converged.DayMarks.AsNoTracking().SingleAsync(m => m.Date == new DateOnly(2026, 9, 22));
        Assert.Equal(DayKind.NoActivity, convergedMark.Kind);
    }

    [Fact]
    public async Task 내보내기는_배포_출석부_템플릿_서식을_따른다()
    {
        await using var host = new ServiceHost();
        host.AsAdmin();
        var kim = await host.SeedUserAsync("김민수", "2025");
        var park = await host.SeedUserAsync("박지훈", "2026");
        await host.Semester.CreateAsync(new SemesterSaveRequest("2026-2", new(2026, 9, 1), new(2026, 12, 31)));
        var first = await host.AdminActivities.CreateAsync(new ActivityCreateRequest(new(2026, 9, 11), DayKind.Completed));
        var second = await host.AdminActivities.CreateAsync(new ActivityCreateRequest(new(2026, 9, 18), DayKind.Completed));
        await host.AdminActivities.SetAttendanceAsync(first.Key!, kim, new ActivityAttendanceRequest(true));
        await host.AdminActivities.SetAttendanceAsync(second.Key!, park, new ActivityAttendanceRequest(true));

        var bytes = await host.Archive.ExportAsync();

        using var zip = new ZipArchive(new MemoryStream(bytes));
        using var sheet = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        var xml = sheet.ReadToEnd();
        Assert.Contains("<t>2학기 총 출석</t>", xml);                 // 학기명 총 출석 헤더
        Assert.Contains("미출석", xml);                                // 완료 열 미출석 명시
        Assert.Contains("""<f>COUNTIF(B3:C3,"출석")</f>""", xml);   // 월 출석횟수 COUNTIF 수식
        Assert.Contains("<f>D3</f>", xml);                            // 총 출석 = 월 집계 합 수식
        Assert.Contains("state=\"frozen\"", xml);                      // 틀 고정 B1
        Assert.Contains("width=\"13.89\"", xml);                       // 이름 열 너비
    }

    [Fact]
    public async Task 내보내기는_학기_소속_부원_전체를_행으로_만든다()
    {
        await using var host = new ServiceHost();
        host.AsAdmin();
        var kim = await host.SeedUserAsync("김민수", "2025");
        var park = await host.SeedUserAsync("박지훈", "2026"); // 0출석 — 가입일(2026-03-02)이 학기 안
        var manager = await host.SeedUserAsync("김관리", "1");
        await using (var db = host.Db.CreateContext())
        {
            var adminRole = db.Roles.Single(r => r.Name == "ADMIN");
            db.UserRoles.Add(new IdentityUserRole<string> { UserId = manager, RoleId = adminRole.Id });
            await db.SaveChangesAsync();
        }
        await host.Semester.CreateAsync(new SemesterSaveRequest("2026-1", new(2026, 2, 1), new(2026, 7, 31)));
        var activity = await host.AdminActivities.CreateAsync(new ActivityCreateRequest(new(2026, 3, 6), DayKind.Completed));
        await host.AdminActivities.SetAttendanceAsync(activity.Key!, kim, new ActivityAttendanceRequest(true));

        var bytes = await host.Archive.ExportAsync();

        using var read = new MemoryStream(bytes);
        var tab = Assert.Single(new ActivityWorkbookReader().Read(read));
        var labels = tab.Rows.Skip(2).Select(r => r.Count > 0 ? r[0] : "").ToList();
        Assert.Contains(labels, l => l.StartsWith("박지훈"));           // 0출석 가입 부원도 행으로
        Assert.DoesNotContain(labels, l => l.StartsWith("김관리"));   // ADMIN 계정은 가입일 행에서 제외
        var parkRow = tab.Rows.Skip(2).Single(r => r.Count > 0 && r[0].StartsWith("박지훈"));
        Assert.Equal("미출석", parkRow[1]);                            // 완료 열 미출석 명시
    }

    // ===== 예정 표시일 시작·라벨 활동 =====

    [Fact]
    public async Task 예정_표시된_날에_활동을_시작하면_마크가_활동으로_대체된다()
    {
        await using var host = new ServiceHost();
        var admin = await host.SeedUserAsync("김회장", "admin");
        host.AsAdmin(admin);
        var today = Domain.KstClock.Today(host.Time);

        await host.AdminActivities.CreateAsync(new ActivityCreateRequest(today, DayKind.Scheduled));
        await using (var db = host.Db.CreateContext()) Assert.Single(await db.DayMarks.ToListAsync());

        var session1 = await host.Activity.StartNewActivityAsync();

        await using var db2 = host.Db.CreateContext();
        Assert.Single(await db2.Activities.ToListAsync()); // 그 날 활동은 정확히 1개
        Assert.Empty(await db2.DayMarks.Where(m => m.Date == today).ToListAsync()); // 예정 표시는 실제 활동으로 대체

        // 마감 후 새 세션 시작도 같은 날 중복 없이 허용된다
        await host.Activity.CloseActivityAsync();
        var session2 = await host.Activity.StartNewActivityAsync();
        Assert.NotEqual(session1.ActivityId, session2.ActivityId);
        await using var db3 = host.Db.CreateContext();
        Assert.Equal(2, await db3.Activities.CountAsync(a => a.ActivityDate == today));
        Assert.Equal(1, await db3.Activities.CountAsync(a => a.ActivityDate == today && a.Status != ActivityStatus.ActivityClosed));
        Assert.Empty(await db3.DayMarks.ToListAsync());
    }

    [Fact]
    public async Task 라벨_활동은_정기_활동과_공존하고_중복_라벨은_거부된다()
    {
        await using var host = new ServiceHost();
        host.AsAdmin();
        var date = new DateOnly(2026, 9, 4);
        var member = await host.SeedUserAsync("김민수", "2025");

        var ot = await host.AdminActivities.CreateAsync(new ActivityCreateRequest(date, DayKind.Completed, Label: "OT"));
        Assert.NotNull(ot.ActivityId);
        Assert.Equal($"app:{ot.ActivityId}", ot.Key);
        Assert.Equal("OT", ot.Label);
        Assert.Equal("활동 완료(OT)", ot.StateText);

        var regular = await host.AdminActivities.CreateAsync(new ActivityCreateRequest(date, DayKind.Completed));
        Assert.NotEqual(ot.ActivityId, regular.ActivityId); // 같은 날 정기 활동과 공존
        Assert.Null(regular.Label);

        await Assert.ThrowsAsync<ConflictException>(() =>
            host.AdminActivities.CreateAsync(new ActivityCreateRequest(date, DayKind.Completed, Label: "OT"))); // 중복 라벨 거부

        // 라벨 활동에도 출석을 부여할 수 있다
        await host.AdminActivities.SetAttendanceAsync(ot.Key!, member, new ActivityAttendanceRequest(true));
        var row = (await host.AdminActivities.GetActivitiesAsync()).Single(r => r.Key == ot.Key);
        Assert.Equal(1, row.AttendanceCount);
        Assert.True(row.CanEditAttendance);

        await using var db = host.Db.CreateContext();
        Assert.Equal(2, await db.Activities.CountAsync(a => a.ActivityDate == date));
        var saved = await db.Activities.AsNoTracking().SingleAsync(a => a.Id == ot.ActivityId);
        Assert.Equal("OT", saved.DisplayName);
        Assert.Equal(ActivityStatus.ActivityClosed, saved.Status);
    }
}
