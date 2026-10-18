namespace VolleyballClub.Application.Abstractions;

/// <summary>시트 동기화 결과 요약 — 시작 시 1회 로그로 남긴다.</summary>
public sealed record SheetSyncReport(
    int ImportedActivities,
    int ImportedAttendances,
    int SkippedUnmatchedRows,
    int ExportedColumns,
    IReadOnlyList<string> Warnings);

/// <summary>단일 활동 시트 즉시 기록 결과. Success=false면 Error에 실패 사유가 담긴다.</summary>
public sealed record SheetPushResult(bool Success, bool Written, string? Error);

/// <summary>시트 미반영 현황 — 관리자 알림·백업 다운로드 판단용.</summary>
public sealed record SheetSyncStatusDto(int PendingCount, IReadOnlyList<SheetPendingActivityDto> Pending, bool RecoveryRequired = false);

public sealed record SheetPendingActivityDto(
    int ActivityId,
    DateOnly ActivityDate,
    string State,
    string? Error,
    DateTime? PendingSince = null,
    SheetPendingReason Reason = SheetPendingReason.Other,
    int? SheetId = null,
    string? TabName = null,
    IReadOnlyList<SheetUnmappedMemberDto>? UnmappedMembers = null);

/// <summary>MappingMissing면 팝업에서 행 연결/추가로 즉시 해결할 수 있다. Other는 안내 문구+백업.</summary>
public enum SheetPendingReason { MappingMissing, Other }

public sealed record SheetUnmappedMemberDto(string UserId, string Name);

public interface ISheetSyncService
{
    /// <summary>시작 시 1회: 미반영(마감 미기록·출석 변경) 활동을 시트에 먼저 기록해 복구한 뒤,
    /// 미반이 없을 때만 시트 과거 탭 → 마감 상태 Activity + Attendance 캐싱(시트가 원천)한다.</summary>
    Task<SheetSyncReport> SyncAsync(CancellationToken ct = default);

    /// <summary>출석 변경(OTP 체크·관리자 수정)·활동 시작/마감 커밋 직후 해당 활동의 라이브/마감 열을
    /// 즉시 시트에 기록한다. DB 반영은 이미 완료된 상태 — 실패해도 롤백하지 않고 미반영(pending)으로
    /// 보존하며 false 대신 결과로만 알린다(관리자 알림·백업 다운로드로 이어진다).</summary>
    Task<SheetPushResult> PushActivityAsync(int activityId, CancellationToken ct = default);

    /// <summary>마지막 요청 내 즉시 기록 결과 — 관리자 응답의 실패 알림에만 사용한다.</summary>
    SheetPushResult? LastPushResult { get; }

    /// <summary>마감 미기록 활동을 내보낸다. 이미 기록된 활동은 변경하지 않는다.</summary>
    Task<bool> ExportClosedActivityAsync(int activityId, CancellationToken ct = default);

    /// <summary>활동 시작 직후 예정 열을 기록한다.</summary>
    Task<bool> ExportScheduledActivityAsync(int activityId, CancellationToken ct = default);

    /// <summary>활동 취소 직후 마감으로 기록했던 출석 정보를 시트에서 제거한다(마크 공란·완료→없음).
    /// 취소는 롤백하지 않고, 실패는 false로만 알린다.</summary>
    Task<bool> RemoveCancelledActivityAsync(int activityId, CancellationToken ct = default);

    /// <summary>관리자 '시트 갱신': 진행 중 활동 현재 출석 임시 기록 + 미내보낸 활동 내보내기 후,
    /// 미반이 없을 때만 시트를 원천으로 전체 재반영(출석 병합 — 수기 수정이 서버에 그대로 반영된다).</summary>
    Task<SheetSyncReport> ResyncAsync(CancellationToken ct = default);

    /// <summary>시트에 아직 반영되지 않은 활동/출석 현황.</summary>
    Task<SheetSyncStatusDto> GetStatusAsync(CancellationToken ct = default);
}
