using VolleyballClub.Domain.Enums;

namespace VolleyballClub.Application.Dtos;

public record AuditLogDto(
    int Id,
    DateTime CreatedAt,
    string AdminName,
    AdminAction Action,
    string TargetType,
    string TargetId,
    string Details);
