using System.Text.RegularExpressions;
using VolleyballClub.Application.Abstractions;
using VolleyballClub.Application.Dtos;

namespace VolleyballClub.Infrastructure.Services;

/// <summary>모든 학기 탭에 공통 적용되는 출석부 서식 전략(서식 명세: Design/sheet-formats.md).
/// 1행 헤더, 0열 부원 라벨, "1"=출석. 날짜 열은 헤더 정규식으로 판별하고
/// 학기별 차이(라벨 접미사·날짜 0채움)는 데이터에서 자동 추론한다.</summary>
public sealed partial class SemesterTabFormat(string tabName, DateOnly from, DateOnly to) : ISemesterSheetFormat
{
    private static readonly string[] DowKo = ["일", "월", "화", "수", "목", "금", "토"];

    public string TabName => tabName;

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

    public SheetSemesterData Parse(SheetTab tab)
    {
        var activities = new List<SheetActivityColumn>();
        foreach (var (date, col) in DateColumns(tab))
        {
            var attended = new List<string>();
            for (var row = 1; row < tab.Rows.Count; row++)
            {
                var cells = tab.Rows[row];
                var label = cells.Count > 0 ? cells[0].Trim() : "";
                var cell = col - 1; // DateColumns는 1-based 열 번호를 돌려준다
                if (cell < cells.Count && cells[cell].Trim() == "1" && label.Length > 0)
                {
                    attended.Add(label);
                }
            }

            // 전원 불참(우천 취소·공휴일 등) 열은 활동으로 가져오지 않는다
            if (attended.Count > 0)
            {
                activities.Add(new SheetActivityColumn(date, attended));
            }
        }

        return new SheetSemesterData(activities);
    }

    public SheetWriteTarget BuildColumn(SheetTab tab, DateOnly activityDate, IReadOnlySet<string> attendedRowLabels)
    {
        var dateColumns = DateColumns(tab).ToList();
        var marks = BuildMarks(tab, attendedRowLabels);

        var exact = dateColumns.FirstOrDefault(c => c.Date == activityDate);
        if (exact.Date == activityDate)
        {
            // 기존 날짜 열에 기록 — 헤더는 그대로 두고 부원 행(2행~)부터 쓴다
            return new SheetWriteTarget(false, exact.Column, new SheetColumn(2, marks));
        }

        // 삽입 위치: 활동 날짜보다 큰 첫 날짜 열 앞, 이후 날짜 열이 없으면 마지막 날짜 열 뒤(집계 열 앞)
        var insertAt = dateColumns.FirstOrDefault(c => c.Date > activityDate).Column;
        if (insertAt == 0)
        {
            insertAt = (dateColumns.Count > 0 ? dateColumns[^1].Column : 1) + 1;
        }

        return new SheetWriteTarget(true, insertAt, new SheetColumn(1, [BuildHeaderText(tab, activityDate), .. marks]));
    }

    private static IEnumerable<(DateOnly Date, int Column)> DateColumns(SheetTab tab)
    {
        if (tab.Rows.Count == 0)
        {
            yield break;
        }

        var header = tab.Rows[0];
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

    [GeneratedRegex(@"(\d{2})\.\s*(\d{1,2})\.\s*(\d{1,2})\.")]
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

    /// <summary>부원 행(2행~)마다 출석 표시를 만든다 — 시트 행과 1:1 정렬(빈 라벨 행은 빈칸).</summary>
    private static IReadOnlyList<string> BuildMarks(SheetTab tab, IReadOnlySet<string> attendedRowLabels)
    {
        var marks = new List<string>();
        for (var row = 1; row < tab.Rows.Count; row++)
        {
            var label = tab.Rows[row].Count > 0 ? tab.Rows[row][0].Trim() : "";
            marks.Add(label.Length > 0 && attendedRowLabels.Contains(label) ? "1" : "");
        }

        return marks;
    }

    /// <summary>삽입할 헤더 텍스트 — 그 탭 기존 날짜 헤더의 0채움 서식을 따른다(예: "26. 10. 12. 월").</summary>
    private static string BuildHeaderText(SheetTab tab, DateOnly date)
    {
        var zeroPad = true;
        if (tab.Rows.Count > 0)
        {
            var sample = tab.Rows[0].FirstOrDefault(DateHeaderRegex().IsMatch);
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
