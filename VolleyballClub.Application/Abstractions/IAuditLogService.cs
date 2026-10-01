using VolleyballClub.Application.Dtos;
using VolleyballClub.Domain.Enums;

namespace VolleyballClub.Application.Abstractions;

public interface IAuditLogService
{
    Task LogAsync(AdminAction action, string targetType, string targetId, string details);

    Task<List<AuditLogDto>> GetRecentAsync(int take = 100);
}
