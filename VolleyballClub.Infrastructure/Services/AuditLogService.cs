using Microsoft.EntityFrameworkCore;
using VolleyballClub.Application.Abstractions;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Domain;
using VolleyballClub.Domain.Entities;
using VolleyballClub.Domain.Enums;
using VolleyballClub.Infrastructure.Identity;
using VolleyballClub.Infrastructure.Persistence;

namespace VolleyballClub.Infrastructure.Services;

public class AuditLogService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ICurrentUserService currentUser,
    TimeProvider timeProvider) : IAuditLogService
{
    public async Task LogAsync(AdminAction action, string targetType, string targetId, string details)
    {
        var adminId = await currentUser.GetUserIdAsync() ?? "system";
        await using var db = await dbFactory.CreateDbContextAsync();
        db.AuditLogs.Add(new AuditLog
        {
            AdminUserId = adminId,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            Details = details,
            CreatedAt = KstClock.NowUtc(timeProvider),
        });
        await db.SaveChangesAsync();
    }

    public async Task<List<AuditLogDto>> GetRecentAsync(int take = 100)
    {
        await currentUser.RequireAdminAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.AuditLogs.AsNoTracking()
            .OrderByDescending(l => l.CreatedAt)
            .Take(take)
            .Join(db.Users,
                l => l.AdminUserId,
                u => u.Id,
                (l, u) => new AuditLogDto(l.Id, l.CreatedAt, u.Name, l.Action, l.TargetType, l.TargetId, l.Details))
            .ToListAsync();
    }
}
