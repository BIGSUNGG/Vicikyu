using System.Text.RegularExpressions;
using VolleyballClub.Application.Abstractions;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Domain.Enums;

namespace VolleyballClub.Infrastructure.Services;

/// <summary>모든 학기 탭에 공통 적용되는 출석부 서식 전략(서식 명세: Design/sheet-formats.md).
/// 두 서식을 자동 감지한다(레이아웃은 Layout 참조):
/// 구형(2023-2~2026-1) — 1행 헤더, 0열 부원 라벨, "1"=출석.
/// 신형(2026-2~) — 1행 활동 상태 Enum(활동 예정/완료/없음(사유)), 2행 헤더, 부원 칸 "출석"/"미출석"/"활동 불가".
/// 학기별 차이(라벨 접미사·날짜 0채움)는 데이터에서 자동 추론한다.</summary>
public sealed partial class SemesterTabFormat(string tabName, DateOnly from, DateOnly to) : ISemesterSheetFormat
{
    private static readonly string[] DowKo = ["일", "월", "화", "수", "목", "금", "토"];

    private const string StatusScheduled = "활동 예정";
    private const string StatusCompleted = "활동 완료";
    private const string StatusNone = "활동 없음";
    private const string MarkAttendedNew = "출석";
    private const string MarkMissedNew = "미출석";

    public string TabName => tabName;

    public DateOnly From => from;

    public DateOnly To => to;

    /// <summary>등록된 전 학기 전략(Design/sheet-formats.md) — 새 학기는 여기에 한 줄 추가한다.</summary>
    public static IReadOnlyList<SemesterTabFormat> All() =>
    [
        new SemesterTabFormat("2023-2", new DateOnly(2023, 8, 1), new DateOnly(2024, 1, 31)),
        new SemesterTabFormat("2024-1", new DateOnly(2024, 2, 1), new DateOnly(2024, 7, 31)),
        new SemesterTabFormat("2024-2", new DateOnly(2024, 8, 1), new DateOnly(2025, 1, 31)),
        new SemesterTabFormat("2025-1", new DateOnly(2025, 2, 1), new DateOnly(2025, 7, 31)),
        new SemesterTabFormat("2025-2", new DateOnly(2025, 8, 1), new DateOnly(2026, 1, 31)),
        new SemesterTabFormat("2026-1", new DateOnly(2026, 2, 1), new DateOnly(2026, 7, 31)),
        new SemesterTabFormat("2026-2", new DateOnly(2026, 8, 1), new DateOnly(2027, 1, 31)),
    ];

    public bool CoversDate(DateOnly date) => date >= from && date <= to;

    /// <summary>탭 레이아웃 — 신형이면 (헤더 행 인덱스, 상태 행), 구형이면 (0, null).</summary>
    private static (int HeaderIndex, IReadOnlyList<string>? StatusRow) Layout(SheetTab tab)
    {
        if (tab.Rows.Count > 0 && IsStatusRow(tab.Rows[0]))
        {
            return (1, tab.Rows[0]);
        }

        return (0, null);
    }

    /// <summary>활동 상태 Enum 값이 하나라도 있으면 신형 상태 행이다.</summary>
    private static bool IsStatusRow(IReadOnlyList<string> row) =>
        row.Any(c =>
        {
            var t = c.Trim();
            return t is StatusScheduled or StatusCompleted or "활동 상태" || t.StartsWith(StatusNone);
        });

    private static string Cell(IReadOnlyList<string> row, int index0) =>
        row.Count > index0 ? row[index0].Trim() : "";

    /// <summary>부원 구간의 끝(미포함) 행 인덱스 — '총 참여자 수'(없으면 '탈퇴/퇴출자') 라벨 행부터는
    /// 집계·명단 구간이라 부원으로 읽지 않는다. 둘 다 없으면 시트 끝까지(탈퇴자가 부원 사이에 섞인 구 탭 호환).</summary>
    private static int MemberEndRow(SheetTab tab, int headerIndex)
    {
        var end = tab.Rows.Count;
        for (var row = headerIndex + 1; row < end; row++)
        {
            var label = tab.Rows[row].Count > 0 ? tab.Rows[row][0].Trim() : "";
            if (label is "총 참여자 수" or "탈퇴/퇴출자")
            {
                return row;
            }
        }

        return end;
    }

    // Shared by parsing, mapping choices and managed row insertion (zero-based, end exclusive).
    internal static (int Start, int End) MemberRegion(SheetTab tab)
    {
        var (header, _) = Layout(tab);
        return (header + 1, MemberEndRow(tab, header));
    }

    public SheetSemesterData Parse(SheetTab tab)
    {
        var (headerIndex, statusRow) = Layout(tab);
        var attendedMark = statusRow is not null ? MarkAttendedNew : "1";
        var activities = new List<SheetActivityColumn>();
        foreach (var (date, col) in DateColumns(tab, headerIndex))
        {
            var attended = new List<string>();
            for (var row = headerIndex + 1; row < MemberEndRow(tab, headerIndex); row++)
            {
                var cells = tab.Rows[row];
                var label = cells.Count > 0 ? cells[0].Trim() : "";
                var cell = col - 1;
                if (cell < cells.Count && cells[cell].Trim() == attendedMark && label.Length > 0)
                {
                    attended.Add(label);
                }
            }

            // 신형은 상태행이 원천: 명시적 완료/없음/예정을 그대로 읽고, 상태가 빈칸인 날짜 열은
            // 출석 마크가 있으면 완료로, 없으면 의도 불명이라 가져오지 않는다(미리 만들어진 빈 열 보호).
            // 구형의 미표시 빈 날짜는 목록에만 예정으로 표시하고 완료 분모에서 제외한다.
            SheetDayKind kind;
            if (statusRow is null)
            {
                var header = Cell(tab.Rows[headerIndex], col - 1);
                kind = header.Contains(StatusNone) ? SheetDayKind.NoActivity
                    : header.Contains(StatusScheduled) ? SheetDayKind.Scheduled
                    : header.Contains(StatusCompleted) || attended.Count > 0 ? SheetDayKind.Completed : SheetDayKind.Scheduled;
            }
            else
            {
                var status = Cell(statusRow, col - 1); // DateColumns는 1-based 열 번호를 돌려준다
                if (status.Contains(StatusNone))
                {
                    kind = SheetDayKind.NoActivity;
                }
                else if (status.Contains(StatusScheduled))
                {
                    kind = SheetDayKind.Scheduled;
                }
                else if (status.Length > 0 || attended.Count > 0)
                {
                    kind = SheetDayKind.Completed; // 명시적 완료, 또는 상태 빈칸 + 출석 마크 존재
                }
                else
                {
                    continue; // 상태 빈칸 + 출석 0 — 가져오지 않는다
                }
            }

            // 명시적 완료는 출석 0명이어도 유지하고, 구형의 미표시 빈 열도 목록에 담는다.
            var statusText = statusRow is null ? Cell(tab.Rows[headerIndex], col - 1) : Cell(statusRow, col - 1);
            var reason = kind == SheetDayKind.NoActivity
                ? statusText[(statusText.IndexOf(StatusNone, StringComparison.Ordinal) + StatusNone.Length)..].Trim() : null;
            if (reason is not null && reason.StartsWith('(') && reason.EndsWith(')')) reason = reason[1..^1].Trim();
            activities.Add(new SheetActivityColumn(date, attended, kind, col, string.IsNullOrEmpty(reason) ? null : reason));
        }

        return new SheetSemesterData(activities);
    }

    /// <summary>'총 참여자 수' 라벨 행의 1-based 행 번호 — 없으면 0(수식 미기록).</summary>
    private static int TotalsRow(SheetTab tab, int headerIndex)
    {
        for (var row = headerIndex + 1; row < tab.Rows.Count; row++)
        {
            var label = tab.Rows[row].Count > 0 ? tab.Rows[row][0].Trim() : "";
            if (label == "총 참여자 수")
            {
                return row + 1;
            }
        }

        return 0;
    }

    /// <summary>삽입 열의 총 참여자 수 수식 — 신형 COUNTIF("출석"), 구형 SUM("1").</summary>
    private static string TotalsFormula(int column1Based, int firstMemberRow, int lastMemberRow, bool isNew)
    {
        var letter = ColumnLetter(column1Based);
        return isNew
            ? $"=COUNTIF({letter}{firstMemberRow}:{letter}{lastMemberRow},\"출석\")"
            : $"=SUM({letter}{firstMemberRow}:{letter}{lastMemberRow})";
    }

    private static string ColumnLetter(int column)
    {
        var letters = "";
        while (column > 0)
        {
            letters = (char)('A' + (column - 1) % 26) + letters;
            column = (column - 1) / 26;
        }

        return letters;
    }

    /// <summary>삽입 경로의 부가 열 — 총 참여자 수 행 수식(있는 탭만).</summary>
    private static SheetColumn? TotalsColumn(SheetTab tab, int headerIndex, int insertAt, int memberCount, bool isNew)
    {
        var totalsRow = TotalsRow(tab, headerIndex);
        if (totalsRow == 0)
        {
            return null;
        }

        var firstMember = headerIndex + 2; // 1-based
        var lastMember = firstMember + memberCount - 1;
        return new SheetColumn(totalsRow, [TotalsFormula(insertAt, firstMember, lastMember, isNew)]);
    }

    public SheetWriteTarget BuildColumn(SheetTab tab, DateOnly activityDate, IReadOnlySet<string> attendedRowLabels)
    {
        var (headerIndex, statusRow) = Layout(tab);
        var isNew = statusRow is not null;
        var dateColumns = DateColumns(tab, headerIndex).ToList();
        var marks = BuildMarks(tab, headerIndex + 1, attendedRowLabels, isNew);

        var exact = dateColumns.FirstOrDefault(c => c.Date == activityDate);
        if (exact.Date == activityDate)
        {
            // 기존 날짜 열에 기록 — 신형은 헤더(2행)를 건너뛰기 위해 상태행 갱신을 별도 열 쓰기로 분리한다
            return isNew
                ? new SheetWriteTarget(false, exact.Column, new SheetColumn(headerIndex + 2, marks),
                    new SheetColumn(1, [StatusCompleted]))
                : new SheetWriteTarget(false, exact.Column, new SheetColumn(2, marks));
        }

        // 삽입 위치: 활동 날짜보다 큰 첫 날짜 열 앞, 이후 날짜 열이 없으면 마지막 날짜 열 뒤(집계 열 앞)
        var insertAt = dateColumns.FirstOrDefault(c => c.Date > activityDate).Column;
        if (insertAt == 0)
        {
            insertAt = (dateColumns.Count > 0 ? dateColumns[^1].Column : 1) + 1;
        }

        // 신형 삽입 열은 상태(1행)+헤더(2행)+부원(3행~)이 연속이라 한 덩어리로 쓰고,
        // 총 참여자 수 행 수식은 삽입 후 별도 쓰기(StatusColumn)로 채운다
        string[] cells = isNew
            ? [StatusCompleted, BuildHeaderText(tab, headerIndex, activityDate), .. marks]
            : [BuildHeaderText(tab, headerIndex, activityDate), .. marks];
        return new SheetWriteTarget(true, insertAt, new SheetColumn(1, cells),
            TotalsColumn(tab, headerIndex, insertAt, marks.Count, isNew));
    }

    /// <summary>활동 날짜의 시트 열 번호(1-based) — 없으면 null. 취소 시 열 삭제에 쓴다.</summary>
    public int? FindDateColumnIndex(SheetTab tab, DateOnly date)
    {
        var (headerIndex, _) = Layout(tab);
        var exact = DateColumns(tab, headerIndex).FirstOrDefault(c => c.Date == date);
        return exact.Date == date ? exact.Column : null;
    }

    public SheetWriteTarget? BuildLiveColumn(SheetTab tab, DateOnly activityDate, IReadOnlySet<string> attendedRowLabels)
    {
        var (headerIndex, statusRow) = Layout(tab);
        if (statusRow is null)
        {
            return null; // 구형 서식 — 상태 개념이 없어 진행 중 임시 기록을 하지 않는다(마감 때만 기록)
        }
        var dateColumns = DateColumns(tab, headerIndex).ToList();
        // 진행 중 마크: 출석자만 표시하고 미출석자는 공란(마감 전에는 아직 출석할 수 있으므로 — 예정 열 공란 규칙)
        var marks = new List<string>();
        for (var row = headerIndex + 1; row < MemberEndRow(tab, headerIndex); row++)
        {
            var label = tab.Rows[row].Count > 0 ? tab.Rows[row][0].Trim() : "";
            marks.Add(label.Length > 0 && attendedRowLabels.Contains(label) ? MarkAttendedNew : "");
        }

        var exact = dateColumns.FirstOrDefault(c => c.Date == activityDate);
        if (exact.Date == activityDate)
        {
            var current = Cell(statusRow, exact.Column - 1);
            if (current.Contains(StatusCompleted) || current.Contains(StatusNone))
            {
                return null; // 이미 확정된 상태를 예정으로 되돌리지 않는다
            }

            return new SheetWriteTarget(false, exact.Column,
                new SheetColumn(headerIndex + 2, marks), new SheetColumn(1, [StatusScheduled]));
        }

        var insertAt = dateColumns.FirstOrDefault(c => c.Date > activityDate).Column;
        if (insertAt == 0)
        {
            insertAt = (dateColumns.Count > 0 ? dateColumns[^1].Column : 1) + 1;
        }

        string[] cells = [StatusScheduled, BuildHeaderText(tab, headerIndex, activityDate), .. marks];
        return new SheetWriteTarget(true, insertAt, new SheetColumn(1, cells),
            TotalsColumn(tab, headerIndex, insertAt, marks.Count, isNew: true));
    }

    private static IEnumerable<(DateOnly Date, int Column)> DateColumns(SheetTab tab, int headerIndex)
    {
        if (tab.Rows.Count <= headerIndex)
        {
            yield break;
        }

        var header = tab.Rows[headerIndex];
        for (var col = 1; col < header.Count; col++)
        {
            var date = TryParseHeaderDate(header[col]);
            if (date is not null)
            {
                // 1-based 열 번호로 반환 — 클라이언트(Write/InsertColumnAsync) 규약과 일치
                yield return (date.Value, col + 1);
            }
        }
    }

    [GeneratedRegex(@"(\d{2})\.\s*(\d{1,2})\.\s*(\d{1,2})")]
    private static partial Regex DateHeaderRegex();

    private static DateOnly? TryParseHeaderDate(string header)
    {
        var match = DateHeaderRegex().Match(header);
        if (!match.Success)
        {
            return null;
        }

        var year = 2000 + int.Parse(match.Groups[1].Value);
        return new DateOnly(year, int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value));
    }

    /// <summary>부원 행마다 출석 표시를 만든다 — 시트 행과 1:1 정렬(빈 라벨 행은 빈칸).
    /// 신형은 출석/미출석을 명시적으로 쓰고, 구형은 "1"/빈칸을 유지한다.</summary>
    private static IReadOnlyList<string> BuildMarks(
        SheetTab tab, int memberStartRow, IReadOnlySet<string> attendedRowLabels, bool isNew)
    {
        var marks = new List<string>();
        for (var row = memberStartRow; row < MemberEndRow(tab, memberStartRow - 1); row++)
        {
            var label = tab.Rows[row].Count > 0 ? tab.Rows[row][0].Trim() : "";
            if (label.Length == 0)
            {
                marks.Add(""); // 빈 라벨 행은 빈칸 — 구형과 동일하게 행 정렬만 유지
                continue;
            }

            marks.Add(isNew
                ? attendedRowLabels.Contains(label) ? MarkAttendedNew : MarkMissedNew
                : attendedRowLabels.Contains(label) ? "1" : "");
        }

        return marks;
    }

    /// <summary>삽입할 헤더 텍스트 — 그 탭 기존 날짜 헤더의 0채움 서식을 따른다(예: "26. 10. 12. 월").</summary>
    private static string BuildHeaderText(SheetTab tab, int headerIndex, DateOnly date)
    {
        var zeroPad = true;
        if (tab.Rows.Count > headerIndex)
        {
            var sample = tab.Rows[headerIndex].FirstOrDefault(DateHeaderRegex().IsMatch);
            if (sample is not null && !Regex.IsMatch(sample, @"\.\s*0\d\.\s*"))
            {
                zeroPad = false;
            }
        }

        var month = zeroPad ? $"{date.Month:D2}" : $"{date.Month}";
        var day = zeroPad ? $"{date.Day:D2}" : $"{date.Day}";
        return $"{date:yy}. {month}. {day}. {DowKo[(int)date.DayOfWeek]}";
    }
}
