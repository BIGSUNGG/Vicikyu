using VolleyballClub.Application.Dtos;

namespace VolleyballClub.Application.Abstractions;

/// <summary>학기별 시트 서식 전략. 매 학기 서식이 달라 구현체를 학기마다 두고
/// Resolver가 탭 이름/활동 날짜로 선택한다. 새 학기는 이 인터페이스 구현체 추가(+DI 등록)만으로 대응된다.</summary>
public interface ISemesterSheetFormat
{
    /// <summary>이 전략이 다루는 시트 탭 이름.</summary>
    string TabName { get; }

    /// <summary>활동 날짜가 이 학기 탭에 기록되는 범위인지.</summary>
    bool CoversDate(DateOnly date);

    /// <summary>탭 원시 데이터 → 활동별 출석 행 라벨 목록. 지각=출석, 불참/공백=제외.</summary>
    SheetSemesterData Parse(SheetTab tab);

    /// <summary>마감 활동 하나를 기록할 위치와 내용을 만든다.
    /// 날짜 열이 이미 있으면 그 열에 덮어쓰기(InsertColumn=false), 없으면 가장 가까운 두 날짜 사이에 삽입.</summary>
    SheetWriteTarget BuildColumn(SheetTab tab, DateOnly activityDate, IReadOnlySet<string> attendedRowLabels);
}

/// <summary>탭 이름/활동 날짜로 학기 전략을 찾는다. 해당하는 것이 없으면 null.</summary>
public interface ISheetFormatResolver
{
    ISemesterSheetFormat? ResolveByTabName(string tabName);

    ISemesterSheetFormat? ResolveByDate(DateOnly date);
}
