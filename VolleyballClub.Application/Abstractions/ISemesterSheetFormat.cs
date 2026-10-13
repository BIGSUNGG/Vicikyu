using VolleyballClub.Application.Dtos;
using VolleyballClub.Domain.Enums;

namespace VolleyballClub.Application.Abstractions;

/// <summary>학기별 시트 서식 전략. 매 학기 서식이 달라 구현체를 학기마다 두고
/// Resolver가 탭 이름/활동 날짜로 선택한다. 새 학기는 이 인터페이스 구현체 추가(+DI 등록)만으로 대응된다.</summary>
public interface ISemesterSheetFormat
{
    /// <summary>이 전략이 다루는 시트 탭 이름.</summary>
    string TabName { get; }

    /// <summary>이 학기 탭이 커버하는 날짜 범위 시작 — 부원 매핑 기간(비회원 학기 판별) 산출에 사용.</summary>
    DateOnly From { get; }

    /// <summary>이 학기 탭이 커버하는 날짜 범위 끝.</summary>
    DateOnly To { get; }

    /// <summary>활동 날짜가 이 학기 탭에 기록되는 범위인지.</summary>
    bool CoversDate(DateOnly date);

    /// <summary>탭 원시 데이터 → 날짜 열별 활동 상태(완료/예정/없음)와 출석 행 라벨 목록.
    /// 지각=출석, 미출석·활동 불가=기록 없음(흡수). 신형(2026-2)과 구형 탭을 자동 감지한다.</summary>
    SheetSemesterData Parse(SheetTab tab);

    /// <summary>마감 활동 하나를 기록할 위치와 내용을 만든다.
    /// 날짜 열이 이미 있으면 그 열에 덮어쓰기(InsertColumn=false), 없으면 가장 가까운 두 날짜 사이에 삽입.
    /// 신형 서식에서는 상태행 갱신값(활동 완료)이 StatusColumn로 함께 반환된다.</summary>
    SheetWriteTarget BuildColumn(SheetTab tab, DateOnly activityDate, IReadOnlySet<string> attendedRowLabels);

    /// <summary>활동 시작(예정)·진행 중 현재 출석을 신형 탭에 임시 기록할 내용을 만든다 — 상태행은 '활동 예정'.
    /// 구형 탭은 상태 행 없이 마크만, 이미 완료/없음으로 확정된 열이면 null(되돌리지 않는다).
    /// 마감 마크 기록은 BuildColumn을 쓴다. 삽입 시 총 참여자 수 행 수식이 StatusColumn로 함께 나간다.</summary>
    SheetWriteTarget? BuildLiveColumn(SheetTab tab, DateOnly activityDate, IReadOnlySet<string> attendedRowLabels);

    /// <summary>활동 취소 — 그 날짜 열을 시트에서 통째로 제거한다. 열이 없으면 null.</summary>
    int? FindDateColumnIndex(SheetTab tab, DateOnly date);
}

/// <summary>탭 이름/활동 날짜로 학기 전략을 찾는다. 해당하는 것이 없으면 null.</summary>
public interface ISheetFormatResolver
{
    ISemesterSheetFormat? ResolveByTabName(string tabName);

    ISemesterSheetFormat? ResolveByDate(DateOnly date);
}
