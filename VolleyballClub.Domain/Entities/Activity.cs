using VolleyballClub.Domain.Enums;

namespace VolleyballClub.Domain.Entities;

/// <summary>하루의 배구 활동 단위. ActivityDate는 Unique — 하루 기본 활동 1개.</summary>
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

    // ---- 상태 전이 규칙(도메인 규칙) ----

    public bool CanStartAttendance => Status == ActivityStatus.Ready;

    public bool CanCloseAttendance => Status == ActivityStatus.AttendanceOpen;

    public bool CanDrawTeams => Status is ActivityStatus.AttendanceClosed or ActivityStatus.TeamCreated;

    public bool CanConfirmTeams => Status == ActivityStatus.AttendanceClosed;

    public bool CanClose => Status is ActivityStatus.AttendanceOpen or ActivityStatus.AttendanceClosed or ActivityStatus.TeamCreated;

    public bool CanReopen => Status == ActivityStatus.ActivityClosed;
}
