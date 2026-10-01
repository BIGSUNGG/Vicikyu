using Microsoft.AspNetCore.Identity;

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

    public bool IsActive { get; set; } = true;

    public DateTime JoinedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
