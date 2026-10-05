namespace VolleyballClub.Domain.Enums;

public enum AdminAction
{
    ActivityCreate,
    ActivityReopen,
    ActivityClose,
    AttendanceOpen,
    AttendanceClose,
    AttendanceAdd,
    AttendanceRemove,
    OtpChange,
    GuestAdd,
    GuestRemove,
    TeamCreate,
    TeamChange,
    TeamConfirm,
    MemberCreate,
    MemberUpdate,
    MemberDisable,
    MemberEnable,
    FeePeriodCreate,
    FeeStatusChange,
    SettingChange,
    // 주의: 기존 값은 DB에 정수로 저장되므로 중간에 새 값을 삽입하지 않는다 — 새 값은 끝에만 추가
    TeamDelete,
}
