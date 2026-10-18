using VolleyballClub.Domain.Enums;

namespace VolleyballClub.Domain.Entities;

/// <summary>하루의 배구 활동 단위. 같은 날 여러 세션 가능 — 현재 활동은 미종료 최신 것.
/// 활동 종료는 관리자 마감으로만 결정되며 날짜 경과로 자동 종료되지 않는다.</summary>
public class Activity
{
    public int Id { get; set; }

    public DateOnly ActivityDate { get; set; }

    public ActivityStatus Status { get; set; } = ActivityStatus.Ready;

    /// <summary>당일 출석용 OTP(숫자 4자리). 관리자 전용으로만 노출된다.</summary>
    public string? OtpCode { get; set; }

    public DateTime? AttendanceOpenedAt { get; set; }

    public DateTime? AttendanceClosedAt { get; set; }

    public DateTime? ActivityClosedAt { get; set; }

    /// <summary>시트 내보내기 완료 시각. null이면 마감됐지만 시트에 아직 기록 안 됨 — 시작 동기화가 복구한다.
    /// 다시 열기(Reopen) 시 null로 초기화되어, 재마감 시 같은 날짜 열을 제자리 덮어써 변경분이 반영된다.</summary>
    public DateTime? SheetExportedAt { get; set; }

    /// <summary>활동·출석 정보가 시트에 반영되지 못한 시각. null이면 미반영 없음.
    /// 시트 우선 가져오기는 이 값이 남은 활동이 없을 때만 진행한다(그 전까지 DB 우선 유지).</summary>
    public DateTime? SheetSyncPendingAt { get; set; }

    /// <summary>마지막 시트 반영 실패 사유. 성공하면 null로 초기화된다.</summary>
    public string? SheetSyncError { get; set; }

    /// <summary>동시 출석이 이전 시트 기록의 성공으로 미반영 해제되지 않도록 하는 변경 버전.</summary>
    public long SheetSyncRevision { get; set; }

    /// <summary>취소된 출석도 행 매핑을 검증할 수 있도록 아직 전송하지 못한 변경 부원을 보존한다.</summary>
    public List<string> SheetSyncRequiredUserIds { get; set; } = [];

    public void MarkSheetPending(DateTime now, string? userId = null)
    {
        SheetSyncRevision++;
        SheetSyncPendingAt = now;
        SheetSyncError = null;
        if (userId is not null && !SheetSyncRequiredUserIds.Contains(userId)) SheetSyncRequiredUserIds.Add(userId);
    }

    public string CreatedByUserId { get; set; } = "";

    /// <summary>시트 가져오기로 생성된 활동의 CreatedByUserId 값 — 해당 학기 탭에 매핑된 부원만 미출석으로 계산하는 판별에 쓰인다.</summary>
    public const string SheetImportCreator = "sheet-import";

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public List<Attendance> Attendances { get; set; } = [];

    public List<Guest> Guests { get; set; } = [];

    public List<Team> Teams { get; set; } = [];

    // ---- 상태 전이 규칙(도메인 규칙): Ready → AttendanceOpen(활동 중, 출석 상시) → TeamCreated(팀 확정, 출석 계속) → ActivityClosed ----

    /// <summary>활동 시작 가능(준비중 → 활동 중).</summary>
    public bool CanStartAttendance => Status == ActivityStatus.Ready;

    /// <summary>팀 편성·확정은 활동 중 언제든 가능.</summary>
    public bool CanDrawTeams => Status is ActivityStatus.AttendanceOpen or ActivityStatus.TeamCreated;

    public bool CanConfirmTeams => Status is ActivityStatus.AttendanceOpen or ActivityStatus.TeamCreated;

    public bool CanClose => Status is ActivityStatus.AttendanceOpen or ActivityStatus.TeamCreated;

    /// <summary>활동 취소(하드 삭제) 가능 여부. 종료된 활동은 다시 열기로 되돌린 뒤 취소한다.</summary>
    public bool CanCancel => Status != ActivityStatus.ActivityClosed;

    public bool CanReopen => Status == ActivityStatus.ActivityClosed;
}
