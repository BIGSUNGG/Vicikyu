using VolleyballClub.Application.Abstractions;

namespace VolleyballClub.Infrastructure.Services;

/// <summary>등록된 학기 전략 중 탭 이름/활동 날짜가 일치하는 첫 번째 전략을 고른다.</summary>
public sealed class SheetFormatResolver(IEnumerable<ISemesterSheetFormat> formats) : ISheetFormatResolver
{
    public ISemesterSheetFormat? ResolveByTabName(string tabName) =>
        formats.FirstOrDefault(format => format.TabName == tabName);

    public ISemesterSheetFormat? ResolveByDate(DateOnly date) =>
        formats.FirstOrDefault(format => format.CoversDate(date));
}
