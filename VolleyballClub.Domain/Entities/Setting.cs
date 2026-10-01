namespace VolleyballClub.Domain.Entities;

/// <summary>키-값 설정(ClubName, SchoolName, FeeSheetUrl, DefaultTeamCount, OtpLength 등).</summary>
public class Setting
{
    public int Id { get; set; }

    public string Key { get; set; } = "";

    public string Value { get; set; } = "";

    public DateTime UpdatedAt { get; set; }
}
