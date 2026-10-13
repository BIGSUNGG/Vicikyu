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
}
