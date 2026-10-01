using VolleyballClub.Domain.Enums;

namespace VolleyballClub.Domain.Entities;

/// <summary>관리자 주요 작업 감사 로그.</summary>
public class AuditLog
{
    public int Id { get; set; }

    public string AdminUserId { get; set; } = "";

    public AdminAction Action { get; set; }

    public string TargetType { get; set; } = "";

    public string TargetId { get; set; } = "";

    public string Details { get; set; } = "";

    public DateTime CreatedAt { get; set; }
}
