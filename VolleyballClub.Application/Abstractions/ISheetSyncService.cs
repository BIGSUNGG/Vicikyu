namespace VolleyballClub.Application.Abstractions;

/// <summary>시트 동기화 결과 요약 — 시작 시 1회 로그로 남긴다.</summary>
public sealed record SheetSyncReport(
    int ImportedActivities,
    int ImportedAttendances,
    int SkippedUnmatchedRows,
    int ExportedColumns,
    IReadOnlyList<string> Warnings);

public interface ISheetSyncService
{
    /// <summary>시작 시 1회: 시트 과거 탭 → 마감 상태 Activity + Attendance 캐싱(기존 날짜 스킵·멱등) +
    /// 시트에 아직 기록되지 않은 마감 활동을 열 추가로 복구한다.</summary>
    Task<SheetSyncReport> SyncAsync(CancellationToken ct = default);

    /// <summary>활동 마감 직후 단일 활동을 현재 학기 탭에 열로 내보낸다.
    /// 실패는 false로만 알린다(마감 롤백 금지 — 다음 시작 SyncAsync가 복구).</summary>
    Task<bool> ExportClosedActivityAsync(int activityId, CancellationToken ct = default);

    /// <summary>활동 시작 직후 신형 탭 상태행에 활동 예정을 기록한다(구형 탭은 기록 없음 — true).
    /// 부가 정보이므로 실패를 복구 대상으로 삼지 않는다.</summary>
    Task<bool> ExportScheduledActivityAsync(int activityId, CancellationToken ct = default);

    /// <summary>활동 취소 직후 마감으로 기록했던 출석 정보를 시트에서 제거한다(마크 공란·완료→없음).
    /// 취소는 롤백하지 않고, 실패는 false로만 알린다.</summary>
    Task<bool> RemoveCancelledActivityAsync(int activityId, CancellationToken ct = default);

    /// <summary>관리자 '시트 갱신': 진행 중 활동 현재 출석 임시 기록 + 미내보낸 마감 내보내기 후
    /// 시트를 원천으로 전체 재반영(출석 병합 — 수기 수정이 서버에 그대로 반영된다).</summary>
    Task<SheetSyncReport> ResyncAsync(CancellationToken ct = default);
}
