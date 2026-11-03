using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using VolleyballClub.Application.Abstractions;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Domain;
using VolleyballClub.Domain.Entities;
using VolleyballClub.Domain.Enums;
using VolleyballClub.Infrastructure.Persistence;

namespace VolleyballClub.Infrastructure.Services;

/// <summary>활동 기록 xlsx 가져오기·내보내기(관리자 설정 페이지).
/// 가져오기: 학기별 탭을 파싱해 DB에 병합한다(멱등 — 같은 파일 재업로드 시 중복 없음).
/// 매칭 실패 부원 행은 UnmappedMember에 임시 보관하고, 완료 활동은 (날짜, 라벨)로 기존 활동에 병합한다.
/// 내보내기: 전체 학기를 탭으로 담은 단일 xlsx(신형 서식 + 월별 출석횟수·학기 총 출석).</summary>
public sealed class ActivityArchiveService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ICurrentUserService currentUser,
    IAuditLogService auditLog,
    TimeProvider timeProvider) : IActivityArchiveService
{
    private static readonly string[] DowKo = ["일", "월", "화", "수", "목", "금", "토"];

    private sealed record MemberRef(string Id, string Name, string StudentNumber);

    private sealed record UserRef(string Name, string StudentNumber, string Department, DateOnly Joined);

    public async Task<ActivityImportReport> ImportAsync(Stream stream, CancellationToken ct = default)
    {
        await currentUser.RequireAdminAsync();
        var tabs = new ActivityWorkbookReader().Read(stream);
        var warnings = new List<string>();
        var now = KstClock.NowUtc(timeProvider);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var members = await db.Users.AsNoTracking()
            .Select(u => new MemberRef(u.Id, u.Name.Trim(), u.StudentNumber.Trim()))
            .ToListAsync(ct);
        var byName = members.Where(m => m.Name.Length > 0).ToLookup(m => m.Name);
        var byNameStudent = members.Where(m => m.Name.Length > 0 && m.StudentNumber.Length > 0)
            .ToLookup(m => $"{m.Name} {m.StudentNumber}");

        // 1단계 — 활동/마크 확정(활동 Id가 필요하므로 먼저 저장한다).
        // 같은 파일 안에서의 중복을 막기 위해 이번 실행의 추가 엔티티를 in-run 집합으로 추적한다
        // — Added 상태 엔티티는 SaveChanges 전까지 DB 쿼리에 보이지 않기 때문이다.
        var pending = new Dictionary<Activity, List<string>>();
        var activityByKey = new Dictionary<(DateOnly Date, string? Label), Activity>();
        var createdActivityDates = new HashSet<DateOnly>();
        var markByDate = new Dictionary<DateOnly, DayMark>();
        var createdMarkDates = new HashSet<DateOnly>();
        var importedActivities = 0;
        var dayMarks = 0;
        foreach (var tab in tabs)
        {
            foreach (var column in SemesterTabParser.Parse(tab).Activities)
            {
                if (column.Kind != DayKind.Completed)
                {
                    // 예정/없음 마크는 그 날짜에 활동이 없을 때만 만든다(왕복 내보내기·재가져오기 불변 유지)
                    var hasActivity = createdActivityDates.Contains(column.Date)
                        || await db.Activities.AnyAsync(a => a.ActivityDate == column.Date, ct);
                    if (hasActivity)
                    {
                        if (column.Kind == DayKind.NoActivity)
                            warnings.Add($"{tab.Name} {column.Date:yyyy-MM-dd}: 활동이 이미 있어 '활동 없음' 표시를 건너뛰었습니다.");
                        continue;
                    }
                    if (createdMarkDates.Contains(column.Date))
                    {
                        // 이번 실행에서 이미 마크를 만든 날짜 — 같은 내용이면 스킵, 다르면 갱신
                        var earlier = markByDate[column.Date];
                        if (earlier.Kind == column.Kind && earlier.Note == column.Reason) continue;
                        earlier.Kind = column.Kind;
                        earlier.Note = column.Reason;
                        continue;
                    }
                    var existing = await db.DayMarks.SingleOrDefaultAsync(m => m.Date == column.Date, ct);
                    if (existing is not null && existing.Kind == column.Kind && existing.Note == column.Reason) continue;
                    if (existing is not null) db.DayMarks.Remove(existing);
                    var mark = new DayMark { Date = column.Date, Kind = column.Kind, Note = column.Reason };
                    db.DayMarks.Add(mark);
                    markByDate[column.Date] = mark;
                    createdMarkDates.Add(column.Date);
                    dayMarks++;
                    continue;
                }

                // 완료 열 — 마크를 실제 활동이 대체한다(이번 실행 추가분은 추적기에서 제거, DB 저장분은 삭제)
                if (markByDate.TryGetValue(column.Date, out var addedMark))
                {
                    db.DayMarks.Remove(addedMark); // Added 상태 제거는 삽입 취소
                    markByDate.Remove(column.Date);
                    createdMarkDates.Remove(column.Date);
                }
                db.DayMarks.RemoveRange(await db.DayMarks.Where(m => m.Date == column.Date).ToListAsync(ct));

                var label = string.IsNullOrWhiteSpace(column.Label) ? null : column.Label.Trim();
                if (activityByKey.TryGetValue((column.Date, label), out var known))
                {
                    if (!pending.TryGetValue(known, out var labels)) pending[known] = labels = [];
                    labels.AddRange(column.AttendedRowLabels);
                    continue;
                }
                var activity = await db.Activities
                    .Where(a => a.ActivityDate == column.Date && a.DisplayName == label)
                    .OrderByDescending(a => a.Id)
                    .FirstOrDefaultAsync(ct);
                if (activity is null)
                {
                    activity = new Activity
                    {
                        ActivityDate = column.Date,
                        DisplayName = label,
                        Status = ActivityStatus.ActivityClosed,
                        CreatedByUserId = Activity.ImportCreator,
                        CreatedAt = now,
                        UpdatedAt = now,
                        AttendanceOpenedAt = now,
                        AttendanceClosedAt = now,
                        ActivityClosedAt = now,
                    };
                    db.Activities.Add(activity);
                    importedActivities++;
                }
                activityByKey[(column.Date, label)] = activity;
                createdActivityDates.Add(column.Date);
                if (!pending.TryGetValue(activity, out var fresh)) pending[activity] = fresh = [];
                fresh.AddRange(column.AttendedRowLabels);
            }
        }
        await db.SaveChangesAsync(ct);

        // 2단계 — 출석 병합(매칭 성공 → Attendance, 실패 → 미매핑 보관)
        var importedAttendances = 0;
        var unmappedMembers = 0;
        var unmappedAttendances = 0;
        var unmappedByLabel = new Dictionary<string, UnmappedMember>();
        var pendingUnmappedPairs = new HashSet<(string Label, int ActivityId)>();
        foreach (var (activity, allRowLabels) in pending)
        {
            var current = await db.Attendances.Where(a => a.ActivityId == activity.Id)
                .Select(a => a.UserId).ToHashSetAsync(ct);
            foreach (var rowLabel in allRowLabels.Distinct(StringComparer.Ordinal))
            {
                var member = ResolveMember(rowLabel, byName, byNameStudent);
                if (member is not null)
                {
                    if (current.Contains(member.Id)) continue;
                    db.Attendances.Add(new Attendance
                    {
                        ActivityId = activity.Id,
                        UserId = member.Id,
                        AttendanceType = AttendanceType.Import,
                        CheckedAt = now,
                        CreatedAt = now,
                    });
                    current.Add(member.Id);
                    importedAttendances++;
                    continue;
                }

                if (!unmappedByLabel.TryGetValue(rowLabel, out var unmapped))
                {
                    unmapped = await db.UnmappedMembers.SingleOrDefaultAsync(m => m.Label == rowLabel, ct);
                    if (unmapped is null)
                    {
                        unmapped = new UnmappedMember { Label = rowLabel, Name = ParseName(rowLabel), CreatedAt = now };
                        db.UnmappedMembers.Add(unmapped);
                        unmappedMembers++;
                        warnings.Add($"{rowLabel}: 부원 매칭 실패 — 미매핑 목록에 보관했습니다.");
                    }
                    unmappedByLabel[rowLabel] = unmapped;
                }
                // 새로 만든 UnmappedMember는 Id가 아직 없으므로 내비게이션으로 연결한다(EF가 저장 시 FK를 채운다)
                var already = unmapped.Id != 0 && await db.UnmappedAttendances.AnyAsync(
                    u => u.UnmappedMemberId == unmapped.Id && u.ActivityId == activity.Id, ct);
                if (already || !pendingUnmappedPairs.Add((rowLabel, activity.Id))) continue;
                db.UnmappedAttendances.Add(new UnmappedAttendance
                {
                    UnmappedMember = unmapped,
                    ActivityId = activity.Id,
                    CreatedAt = now,
                });
                unmappedAttendances++;
            }
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        await auditLog.LogAsync(AdminAction.SettingChange, "ActivityArchive", "import",
            $"활동 기록 가져오기 — 활동 {importedActivities}건·출석 {importedAttendances}건·표시 {dayMarks}건·미매핑 {unmappedMembers}명");
        return new ActivityImportReport(importedActivities, importedAttendances, dayMarks, unmappedMembers, unmappedAttendances, warnings);
    }

    private static string ParseName(string rowLabel) =>
        rowLabel.Trim().Split(' ').FirstOrDefault() ?? rowLabel.Trim();

    /// <summary>행 라벨 → DB 부원 매칭. '이름 학번 학과'는 이름+학번으로 먼저, 학번이 없으면 이름 단독(유일할 때만).
    /// 동명이인 등 모호하면 매칭하지 않고 미매핑으로 보낸다.</summary>
    private static MemberRef? ResolveMember(string rowLabel,
        ILookup<string, MemberRef> byName, ILookup<string, MemberRef> byNameStudent)
    {
        var tokens = rowLabel.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return null;
        var name = tokens[0];
        var student = tokens.Skip(1).FirstOrDefault(t => t.Length is >= 1 and <= 4 && t.All(char.IsAsciiDigit));
        if (student is not null)
        {
            var keyed = byNameStudent[$"{name} {student}"].ToList();
            if (keyed.Count == 1) return keyed[0];
            if (keyed.Count > 1) return null;
        }
        var named = byName[name].ToList();
        return named.Count == 1 ? named[0] : null;
    }

    // ===== 내보내기 =====

    private sealed record ExportItem(DateOnly Date, Activity? Activity, DayMark? Mark);

    private sealed record ExportRow(string Label, string? UserId, HashSet<int> AttendedActivityIds);

    public async Task<byte[]> ExportAsync(CancellationToken ct = default)
    {
        await currentUser.RequireAdminAsync();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var periods = await db.SemesterPeriods.AsNoTracking().OrderBy(p => p.StartDate).ToListAsync(ct);
        var activities = await db.Activities.AsNoTracking().Include(a => a.Attendances)
            .OrderBy(a => a.ActivityDate).ToListAsync(ct);
        var marks = await db.DayMarks.AsNoTracking().OrderBy(m => m.Date).ToListAsync(ct);
        var unmapped = await db.UnmappedMembers.AsNoTracking().Include(m => m.Attendances).ToListAsync(ct);
        var userRows = await db.Users.AsNoTracking()
            .Select(u => new { u.Id, u.Name, u.StudentNumber, u.Department, u.JoinedAt }).ToListAsync(ct);
        var usersById = userRows.ToDictionary(u => u.Id,
            u => new UserRef(u.Name, u.StudentNumber, u.Department, ToKstDate(u.JoinedAt)));
        // ADMIN 역할 계정은 부원 명단이 아니므로 가입일 기준 행에 넣지 않는다(출석이 있으면 출석자 행으로 포함)
        var adminRoleIds = await db.Roles.AsNoTracking().Where(r => r.Name == "ADMIN")
            .Select(r => r.Id).ToListAsync(ct);
        var adminIds = await db.UserRoles.AsNoTracking()
            .Where(ur => adminRoleIds.Contains(ur.RoleId)).Select(ur => ur.UserId).ToHashSetAsync(ct);
        // 학기 귀속 규칙(MemberSemesters와 동일) — 가입일이 학기 안인 부원도 행에 포함
        HashSet<string> SemesterJoiners(DateOnly from, DateOnly to) =>
            new(usersById.Where(kv => kv.Value.Joined >= from && kv.Value.Joined <= to && !adminIds.Contains(kv.Key))
                .Select(kv => kv.Key));

        // 학기별 그룹 — 어느 학기에도 속하지 않은 기록은 '미분류' 탭으로
        var groups = periods.Select(p => (p.Name, p.StartDate, p.EndDate))
            .Where(g => activities.Any(a => a.ActivityDate >= g.Item2 && a.ActivityDate <= g.Item3)
                || marks.Any(m => m.Date >= g.Item2 && m.Date <= g.Item3))
            .OrderByDescending(g => g.Item2) // 최신 학기 탭 먼저(배포 출석부 순서)
            .ToList<(string Name, DateOnly From, DateOnly To)>();
        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        string SafeTabName(string name)
        {
            // 엑셀 시트명 규칙 — 금지 문자 제거·31자 제한·중복은 접미로 구분
            var buffer = new System.Text.StringBuilder();
            foreach (var c in name)
                if (c is not (':' or '\\' or '/' or '?' or '*' or '[' or ']'))
                    buffer.Append(c);
            var cleaned = buffer.ToString().Trim();
            if (cleaned.Length == 0) cleaned = "학기";
            if (cleaned.Length > 31) cleaned = cleaned[..31];
            var unique = cleaned;
            for (var i = 2; !usedNames.Add(unique); i++) unique = $"{cleaned[..Math.Min(28, cleaned.Length)]}({i})";
            return unique;
        }
        var tabs = groups.Select(g => BuildTab(SafeTabName(g.Name), SemesterTotalHeader(g.Name), SemesterJoiners(g.From, g.To),
            activities.Where(a => a.ActivityDate >= g.From && a.ActivityDate <= g.To).ToList(),
            marks.Where(m => m.Date >= g.From && m.Date <= g.To).ToList(),
            unmapped, usersById)).ToList();
        var leftoverActivities = activities.Where(a => !groups.Any(g => a.ActivityDate >= g.From && a.ActivityDate <= g.To)).ToList();
        var leftoverMarks = marks.Where(m => !groups.Any(g => m.Date >= g.From && m.Date <= g.To)).ToList();
        if (leftoverActivities.Count > 0 || leftoverMarks.Count > 0)
            tabs.Add(BuildTab(SafeTabName("미분류"), SemesterTotalHeader("미분류"), [], leftoverActivities, leftoverMarks, unmapped, usersById));

        return ActivityWorkbookWriter.Write(tabs);
    }

    private static (string Name, Cell[][] Grid) BuildTab(string name, string totalHeader, HashSet<string> joinerIds,
        List<Activity> activities, List<DayMark> marks, List<UnmappedMember> unmapped, Dictionary<string, UserRef> users)
    {
        // 열 항목: 같은 날에는 활동이 마크보다 먼저 온다(마크는 같은 날 활동과 공존하지 않지만 정렬 안전장치)
        var items = activities.Select(a => new ExportItem(a.ActivityDate, a, null))
            .Concat(marks.Select(m => new ExportItem(m.Date, null, m)))
            .OrderBy(i => i.Date)
            .ThenBy(i => i.Activity is null ? 1 : 0)
            .ToList();

        // 행: 그 학기 소속 부원 전체(출석 1회 이상 또는 가입일이 학기 안 — MemberSemesters 규칙) + 미매핑 부원(원본 라벨)
        var activityIds = activities.Select(a => a.Id).ToHashSet();
        var rows = activities.SelectMany(a => a.Attendances.Select(x => x.UserId)).Distinct()
            .Concat(joinerIds)
            .Where(users.ContainsKey)
            .Distinct()
            .Select(id => new ExportRow(
                $"{users[id].Name} {users[id].StudentNumber} {users[id].Department}".Trim(),
                id,
                activities.Where(a => a.Attendances.Any(x => x.UserId == id)).Select(a => a.Id).ToHashSet()))
            .Concat(unmapped
                .Where(m => m.Attendances.Any(u => activityIds.Contains(u.ActivityId)))
                .Select(m => new ExportRow(m.Label, null,
                    m.Attendances.Where(u => activityIds.Contains(u.ActivityId)).Select(u => u.ActivityId).ToHashSet())))
            .OrderBy(r => r.Label, StringComparer.CurrentCulture)
            .ToList();

        var grid = new List<Cell[]>();
        var statusCells = new List<Cell> { new("활동 상태") };
        var headerCells = new List<Cell> { new("총 인원 : ") };
        var bodyCells = rows.Select(_ => new List<Cell> { new("") }).ToList();
        var monthCounts = rows.Select(_ => new Dictionary<int, int>()).ToList();
        var totals = new int[rows.Count];
        var lastMonth = -1;
        // Excel 열 번호(1-based) 추적 — A는 부원 라벨 열, 첫 항목 열은 B(=2)
        var col = 2;
        var monthStartCol = 2;
        var countCols = new List<int>();

        void AppendMonthCount(int month)
        {
            statusCells.Add(new(""));
            headerCells.Add(new($"{month}월 출석횟수"));
            var from = ActivityWorkbookWriter.ColumnName(monthStartCol);
            var to = ActivityWorkbookWriter.ColumnName(col - 1);
            for (var r = 0; r < rows.Count; r++)
                bodyCells[r].Add(new((monthCounts[r].GetValueOrDefault(month, 0)).ToString(),
                    Formula: $"COUNTIF({from}{r + 3}:{to}{r + 3},\"출석\")"));
            countCols.Add(col);
            col++;
            monthStartCol = col;
        }

        foreach (var item in items)
        {
            if (lastMonth >= 0 && item.Date.Month != lastMonth) AppendMonthCount(lastMonth);
            lastMonth = item.Date.Month;

            var header = HeaderText(item.Date);
            if (item.Activity is { } activity)
            {
                statusCells.Add(new(activity.Status == ActivityStatus.ActivityClosed ? "활동 완료" : "활동 예정"));
                if (!string.IsNullOrWhiteSpace(activity.DisplayName)) header += $" {activity.DisplayName.Trim()}";
                for (var r = 0; r < rows.Count; r++)
                {
                    var attended = rows[r].AttendedActivityIds.Contains(activity.Id);
                    if (attended) { monthCounts[r][item.Date.Month] = monthCounts[r].GetValueOrDefault(item.Date.Month) + 1; totals[r]++; }
                    bodyCells[r].Add(new(attended ? "출석" : "미출석"));
                }
            }
            else
            {
                var mark = item.Mark!;
                statusCells.Add(new(mark.Kind == DayKind.Scheduled
                    ? "활동 예정"
                    : "활동 없음" + (string.IsNullOrWhiteSpace(mark.Note) ? "" : $"({mark.Note.Trim()})")));
                for (var r = 0; r < rows.Count; r++) bodyCells[r].Add(new(""));
            }
            headerCells.Add(new(header));
            col++;
        }
        if (items.Count > 0)
        {
            AppendMonthCount(lastMonth);
            statusCells.Add(new(""));
            headerCells.Add(new(totalHeader));
            for (var r = 0; r < rows.Count; r++)
                bodyCells[r].Add(new(totals[r].ToString(),
                    Formula: string.Join("+", countCols.Select(c => $"{ActivityWorkbookWriter.ColumnName(c)}{r + 3}"))));
        }

        grid.Add([.. statusCells]);
        grid.Add([.. headerCells]);
        for (var r = 0; r < rows.Count; r++)
            grid.Add([new(rows[r].Label), .. bodyCells[r].Where((c, i) => i > 0)]);
        return (name, [.. grid]);
    }

    /// <summary>학기명 끝 숫자로 총 출석 헤더를 만든다('2026-2' → '2학기 총 출석'). 숫자가 없으면 '학기 총 출석'.</summary>
    private static string SemesterTotalHeader(string periodName)
    {
        var trimmed = periodName.Trim();
        return trimmed.Length > 0 && char.IsAsciiDigit(trimmed[^1]) ? $"{trimmed[^1]}학기 총 출석" : "학기 총 출석";
    }

    private static readonly TimeZoneInfo Kst = ResolveKst();

    private static TimeZoneInfo ResolveKst()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time"); }
    }

    /// <summary>UTC 가입일 → KST 날짜(학기 귀속 판정 — MemberSemesters와 같은 변환).</summary>
    private static DateOnly ToKstDate(DateTime utc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Kst));

    private static string HeaderText(DateOnly date) =>
        $"{date:yy}. {date.Month:D2}. {date.Day:D2}. {DowKo[(int)date.DayOfWeek]}";
}

/// <summary>내보내기 셀 — Numeric이면 숫자 칸, Formula가 있으면 수식+캐시 값 칸, 아니면 인라인 문자열 칸.</summary>
internal readonly record struct Cell(string Value, bool Numeric = false, string? Formula = null);

/// <summary>최소 OOXML xlsx 작성기(인라인 문자열·숫자·수식 셀 + 틀 고정 B1·이름 열 너비 — 배포 출석부 서식).</summary>
internal static class ActivityWorkbookWriter
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace PackageRel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace OfficeRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace ContentTypes = "http://schemas.openxmlformats.org/package/2006/content-types";

    public static byte[] Write(IReadOnlyList<(string Name, Cell[][] Grid)> tabs)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Entry(string path, XElement root)
            {
                var entry = zip.CreateEntry(path, CompressionLevel.Fastest);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), root).Save(writer);
            }

            // [Content_Types].xml
            var types = new XElement(ContentTypes + "Types",
                new XElement(ContentTypes + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
                new XElement(ContentTypes + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                new XElement(ContentTypes + "Override", new XAttribute("PartName", "/xl/workbook.xml"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml")));
            for (var i = 1; i <= tabs.Count; i++)
                types.Add(new XElement(ContentTypes + "Override", new XAttribute("PartName", $"/xl/worksheets/sheet{i}.xml"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml")));
            Entry("[Content_Types].xml", types);

            // _rels/.rels
            Entry("_rels/.rels", new XElement(PackageRel + "Relationships",
                new XElement(PackageRel + "Relationship", new XAttribute("Id", "rId1"),
                    new XAttribute("Type", OfficeRel.NamespaceName + "/officeDocument"), new XAttribute("Target", "xl/workbook.xml"))));

            // xl/workbook.xml
            var sheets = new XElement(Main + "sheets");
            for (var i = 1; i <= tabs.Count; i++)
                sheets.Add(new XElement(Main + "sheet", new XAttribute("name", tabs[i - 1].Name),
                    new XAttribute("sheetId", i.ToString()), new XAttribute(XName.Get("id", OfficeRel.NamespaceName), $"rId{i}")));
            Entry("xl/workbook.xml", new XElement(Main + "workbook", sheets));

            // xl/_rels/workbook.xml.rels
            var workbookRels = new XElement(PackageRel + "Relationships");
            for (var i = 1; i <= tabs.Count; i++)
                workbookRels.Add(new XElement(PackageRel + "Relationship", new XAttribute("Id", $"rId{i}"),
                    new XAttribute("Type", OfficeRel.NamespaceName + "/worksheet"), new XAttribute("Target", $"worksheets/sheet{i}.xml")));
            Entry("xl/_rels/workbook.xml.rels", workbookRels);

            // xl/worksheets/sheetN.xml
            for (var t = 0; t < tabs.Count; t++)
            {
                var sheetData = new XElement(Main + "sheetData");
                for (var r = 0; r < tabs[t].Grid.Length; r++)
                {
                    var rowElement = new XElement(Main + "row", new XAttribute("r", (r + 1).ToString()));
                    for (var c = 0; c < tabs[t].Grid[r].Length; c++)
                    {
                        var cell = tabs[t].Grid[r][c];
                        if (string.IsNullOrEmpty(cell.Value)) continue;
                        var address = $"{ColumnName(c + 1)}{r + 1}";
                        XElement cellElement;
                        if (cell.Formula is not null)
                        {
                            // 수식 + 캐시 값 — Excel에서 열면 재계산되고, 계산하지 않는 뷰어·재가져오기는 캐시 값을 본다
                            cellElement = new XElement(Main + "c", new XAttribute("r", address),
                                new XElement(Main + "f", cell.Formula), new XElement(Main + "v", cell.Value));
                        }
                        else if (cell.Numeric)
                        {
                            cellElement = new XElement(Main + "c", new XAttribute("r", address),
                                new XElement(Main + "v", cell.Value));
                        }
                        else
                        {
                            cellElement = new XElement(Main + "c", new XAttribute("r", address), new XAttribute("t", "inlineStr"),
                                new XElement(Main + "is", new XElement(Main + "t", cell.Value)));
                        }
                        rowElement.Add(cellElement);
                    }
                    sheetData.Add(rowElement);
                }
                Entry($"xl/worksheets/sheet{t + 1}.xml", new XElement(Main + "worksheet",
                    new XElement(Main + "sheetViews",
                        new XElement(Main + "sheetView", new XAttribute("workbookViewId", "0"),
                            new XElement(Main + "pane",
                                new XAttribute("xSplit", "1"), new XAttribute("topLeftCell", "B1"),
                                new XAttribute("activePane", "topRight"), new XAttribute("state", "frozen")))),
                    new XElement(Main + "cols",
                        new XElement(Main + "col", new XAttribute("min", "1"), new XAttribute("max", "1"),
                            new XAttribute("width", "13.89"), new XAttribute("customWidth", "1"))),
                    sheetData));
            }
        }
        return output.ToArray();
    }

    internal static string ColumnName(int column)
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

