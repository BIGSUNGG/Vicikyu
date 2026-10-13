namespace VolleyballClub.Domain.Enums;

/// <summary>부원 활동 상태. 레거시 IsActive=false(비활성)는 마이그레이션에서 Withdrawn으로 변환된다.</summary>
public enum MemberStatus
{
    Active = 0,
    Withdrawn = 1,
    Expelled = 2,
}
