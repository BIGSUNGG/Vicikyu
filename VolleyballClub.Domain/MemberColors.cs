namespace VolleyballClub.Domain;

/// <summary>
/// 부원 색 고정 팔레트 — 유일 소스. 팔레트 밖 색은 저장할 수 없다(셀프/관리자 설정 모두 동일 규칙).
/// 신규 가입·기존 부원 백필은 이 팔레트에서 랜덤 배정한다.
/// </summary>
public static class MemberColors
{
    public static readonly string[] Palette =
    [
        "#0ea5e9", // 하늘
        "#8b5cf6", // 라일락
        "#f59e0b", // 노랑
        "#f43f5e", // 로즈
        "#10b981", // 초록
        "#f97316", // 주황
        "#14b8a6", // 청록
        "#ec4899", // 핑크
        "#6366f1", // 인디고
        "#84cc16", // 라임
    ];

    /// <summary>기본(랜덤) 배정용.</summary>
    public static string PickRandom() => Palette[Random.Shared.Next(Palette.Length)];

    public static bool Contains(string? color) => Palette.Contains(color);
}
