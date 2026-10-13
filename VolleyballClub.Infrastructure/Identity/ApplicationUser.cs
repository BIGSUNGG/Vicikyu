using Microsoft.AspNetCore.Identity;
using VolleyballClub.Domain.Enums;

namespace VolleyballClub.Infrastructure.Identity;

/// <summary>
/// 배구부 부원 계정. UserName(=로그인 학번)과 별도로 StudentNumber 컬럼에 Unique Index가 걸린다.
/// 소셜 로그인(Kakao/Google/Microsoft) 확장은 이 타입 뒤의 Identity 구성에서 추가한다.
/// </summary>
public class ApplicationUser : IdentityUser
{
    public string StudentNumber { get; set; } = "";

    public string Name { get; set; } = "";

    public string Department { get; set; } = "";

    /// <summary>활동 상태(활동/탈퇴/퇴출). 레거시 IsActive를 대체한다 — 비활동 부원은 로그인·출석·팀 편성에서 제외된다.</summary>
    public MemberStatus Status { get; set; } = MemberStatus.Active;

    /// <summary>부원 색(MemberColors 팔레트 값). 미지정(레거시)은 null — 중립색으로 렌더링.</summary>
    public string? Color { get; set; }

    public DateTime JoinedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
