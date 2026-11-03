using System.Text.RegularExpressions;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Domain.Enums;

namespace VolleyballClub.Infrastructure.Services;

/// <summary>학기 탭 출석부 그리드 파서(xlsx 가져오기·내보내기 공통 서식).
/// 두 서식을 자동 감지한다:
/// 구형(2023-2~2026-1) — 1행 날짜 헤더, 0열 부원 라벨, "1"/"O"=출석.
/// 신형(2026-2~) — 1행 활동 상태(활동 예정/완료/없음(사유)), 2행 날짜 헤더, 부원 칸 "출석".
/// 날짜가 아닌 헤더(OT 참여·세미나·회비·월 출석횟수 등 특수·집계 칼럼)는 자동으로 건너뛴다.</summary>
public static partial class SemesterTabParser
{
    private const string StatusScheduled = "활동 예정";
    private const string StatusCompleted = "활동 완료";
    private const string StatusNone = "활동 없음";
    private const string MarkAttendedNew = "출석";

    /// <summary>탭 레이아웃 — 신형이면 (날짜 헤더 행 인덱스, 상태 행), 구형이면 (0, null).</summary>
    private static (int HeaderIndex, IReadOnlyList<string>? StatusRow) Layout(TabGrid tab)
    {
        if (tab.Rows.Count > 0 && IsStatusRow(tab.Rows[0]))
        {
            return (1, tab.Rows[0]);
        }

        return (0, null);
    }

    /// <summary>활동 상태 값이 하나라도 있으면 신형 상태 행이다.</summary>
    private static bool IsStatusRow(IReadOnlyList<string> row) =>
        row.Any(c =>
        {
            var t = c.Trim();
            return t is StatusScheduled or StatusCompleted or "활동 상태" || t.StartsWith(StatusNone);
        });

    private static string Cell(IReadOnlyList<string> row, int index0) =>
        row.Count > index0 ? row[index0]?.Trim() ?? "" : "";

    /// <summary>부원 구간의 끝(미포함) 행 인덱스 — '총 참여자 수'(없으면 '탈퇴/퇴출자') 라벨 행부터는
    /// 집계·명단 구간이라 부원으로 읽지 않는다. 둘 다 없으면 탭 끝까지(탈퇴자가 부원 사이에 섞인 구 탭 호환).</summary>
    private static int MemberEndRow(TabGrid tab, int headerIndex)
    {
        var end = tab.Rows.Count;
        for (var row = headerIndex + 1; row < end; row++)
        {
            var label = tab.Rows[row].Count > 0 ? tab.Rows[row][0]?.Trim() ?? "" : "";
            if (label is "총 참여자 수" or "탈퇴/퇴출자")
            {
                return row;
            }
        }

        return end;
    }

    /// <summary>구형 출석 마크 — "1"(숫자 1 포맷) 또는 "O"(2023-1 O/X 표기). X·빈칸·기타 텍스트는 미출석이다.</summary>
    private static bool IsLegacyAttended(string cell) => cell is "1" or "O";

    public static SemesterTabData Parse(TabGrid tab)
    {
        var (headerIndex, statusRow) = Layout(tab);
        var attendedMark = statusRow is not null ? MarkAttendedNew : "1";
        var activities = new List<ActivityColumnData>();
        foreach (var (date, col, headerText) in DateColumns(tab, headerIndex))
        {
            var attended = new List<string>();
            for (var row = headerIndex + 1; row < MemberEndRow(tab, headerIndex); row++)
            {
                var cells = tab.Rows[row];
                var label = Cell(cells, 0);
                var cell = col - 1;
                var mark = Cell(cells, cell);
                if (label.Length > 0 && (mark == attendedMark || (statusRow is null && IsLegacyAttended(mark))))
                {
                    attended.Add(label);
                }
            }

            // 신형은 상태행이 원천: 명시적 완료/없음/예정을 그대로 읽고, 상태가 빈칸인 날짜 열은
            // 출석 마크가 있으면 완료로, 없으면 의도 불명이라 가져오지 않는다(미리 만들어진 빈 열 보호).
            // 구형의 미표시 빈 날짜는 예정으로만 표시하고 완료 분모에서 제외한다.
            DayKind kind;
            string? note = null;
            if (statusRow is null)
            {
                var header = headerText;
                kind = header.Contains(StatusNone) ? DayKind.NoActivity
                    : header.Contains(StatusScheduled) ? DayKind.Scheduled
                    : header.Contains(StatusCompleted) || attended.Count > 0 ? DayKind.Completed : DayKind.Scheduled;
                if (kind == DayKind.NoActivity) note = ExtractReason(header);
            }
            else
            {
                var status = Cell(statusRow, col - 1);
                if (status.Contains(StatusNone))
                {
                    kind = DayKind.NoActivity;
                    note = ExtractReason(status);
                }
                else if (status.Contains(StatusScheduled))
                {
                    kind = DayKind.Scheduled;
                }
                else if (status.Length > 0 || attended.Count > 0)
                {
                    kind = DayKind.Completed; // 명시적 완료, 또는 상태 빈칸 + 출석 마크 존재
                }
                else
                {
                    continue; // 상태 빈칸 + 출석 0 — 가져오지 않는다
                }
            }

            activities.Add(new ActivityColumnData(date, attended, kind, LabelOf(headerText), note));
        }

        return new SemesterTabData(activities);
    }

    /// <summary>활동 없음 헤더에서 괄호 사유를 뽑는다(예: '활동 없음(추석 연휴)' → '추석 연휴').</summary>
    internal static string? ExtractReason(string statusText)
    {
        var at = statusText.IndexOf(StatusNone, StringComparison.Ordinal);
        if (at < 0) return null;
        var reason = statusText[(at + StatusNone.Length)..].Trim();
        if (reason.StartsWith('(') && reason.EndsWith(')')) reason = reason[1..^1].Trim();
        return string.IsNullOrEmpty(reason) ? null : reason;
    }

    /// <summary>날짜 헤더에서 라벨 활동 이름을 뽑는다(예: '26. 09. 04. 금 OT' → 'OT', '26. 09. 11. 금' → null).</summary>
    internal static string? LabelOf(string headerText)
    {
        var match = DateHeaderRegex().Match(headerText);
        if (!match.Success) return null;
        // 날짜 끝의 구분점(·“26. 09. 04.”의 마지막 점)과 공백을 먼저 덜어낸 뒤 요일 한 글자를 제거한다
        var rest = headerText[(match.Index + match.Length)..].Trim().TrimStart(' ', '.', '-').Trim();
        if (rest.Length > 0 && "일월화수목금토".Contains(rest[0])) rest = rest[1..].TrimStart(' ', '.', '-');
        return string.IsNullOrWhiteSpace(rest) ? null : rest;
    }

    private static IEnumerable<(DateOnly Date, int Column, string HeaderText)> DateColumns(TabGrid tab, int headerIndex)
    {
        if (tab.Rows.Count <= headerIndex)
        {
            yield break;
        }

        var header = tab.Rows[headerIndex];
        for (var col = 1; col < header.Count; col++)
        {
            var text = header[col].Trim();
            if (TryParseHeaderDate(text) is { } date)
            {
                // 1-based 열 번호로 반환
                yield return (date, col + 1, text);
            }
        }
    }

    [GeneratedRegex(@"(\d{2})\.\s*(\d{1,2})\.\s*(\d{1,2})")]
    private static partial Regex DateHeaderRegex();

    /// <summary>'26. 09. 11. 금' 텍스트 또는 엑셀 일련번호(예: 46360) 날짜 헤더 해석.</summary>
    internal static DateOnly? TryParseHeaderDate(string header)
    {
        var match = DateHeaderRegex().Match(header);
        if (match.Success)
        {
            var year = 2000 + int.Parse(match.Groups[1].Value);
            return new DateOnly(year, int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value));
        }

        // 엑셀 1900 날짜제 일련번호 — 신형 탭의 예정 날짜가 숫자로 남아 있는 경우(2026-12 이후 예정일 등)
        if (double.TryParse(header, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var serial)
            && serial is >= 40000 and <= 60000)
        {
            return DateOnly.FromDateTime(DateTime.FromOADate(serial));
        }

        return null;
    }
}
