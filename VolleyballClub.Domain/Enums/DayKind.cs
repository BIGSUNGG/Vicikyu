namespace VolleyballClub.Domain.Enums;

/// <summary>날짜 표시 종류. Completed는 xlsx 파싱 결과에만 쓰이고 DB(DayMark)에는 Scheduled/NoActivity만 저장된다.
/// 과거 날짜의 Scheduled는 활동 데이터 조회·변경 지점에서 NoActivity로 자동 전환된다(스윕 — AdminActivityService).</summary>
public enum DayKind
{
    Completed = 0,
    Scheduled = 1,
    NoActivity = 2,
    // 값 3은 폐기된 '활동 취소'(2026-11-04 실험 빌드에서만 사용) — 재사용 금지
}
