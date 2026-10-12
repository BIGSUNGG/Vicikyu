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

    public string CreatedByUserId { get; set; } = "";

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
