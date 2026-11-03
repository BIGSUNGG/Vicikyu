using Microsoft.EntityFrameworkCore;
using VolleyballClub.Application.Abstractions;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Domain;
using VolleyballClub.Domain.Entities;
using VolleyballClub.Domain.Enums;
using VolleyballClub.Infrastructure.Persistence;

namespace VolleyballClub.Infrastructure.Services;

public sealed class UnmappedMemberService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ICurrentUserService currentUser,
    IAuditLogService auditLog,
    TimeProvider timeProvider) : IUnmappedMemberService
{
    public async Task<IReadOnlyList<UnmappedMemberDto>> GetListAsync(CancellationToken ct = default)
    {
        await currentUser.RequireAdminAsync();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.UnmappedMembers.AsNoTracking()
            .OrderBy(m => m.Name).ThenBy(m => m.Id)
            .Select(m => new UnmappedMemberDto(m.Id, m.Label, m.Name, m.Attendances.Count, m.CreatedAt))
            .ToListAsync(ct);
    }

    public async Task LinkAsync(int unmappedMemberId, string userId, CancellationToken ct = default)
    {
        await currentUser.RequireAdminAsync();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var unmapped = await db.UnmappedMembers.Include(m => m.Attendances)
            .SingleOrDefaultAsync(m => m.Id == unmappedMemberId, ct)
            ?? throw new Application.Exceptions.NotFoundException("미매핑 부원을 찾을 수 없습니다.");
        var member = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new Application.Exceptions.NotFoundException("부원을 찾을 수 없습니다.");
        var now = KstClock.NowUtc(timeProvider);

        foreach (var record in unmapped.Attendances)
        {
            // 이미 해당 계정에 같은 활동 출석이 있으면(가져오기 중복 방지) 이관하지 않고 버린다
            var exists = await db.Attendances.AnyAsync(a => a.ActivityId == record.ActivityId && a.UserId == userId, ct);
            if (!exists)
            {
                db.Attendances.Add(new Attendance
                {
                    ActivityId = record.ActivityId,
                    UserId = userId,
                    AttendanceType = AttendanceType.Import,
                    CheckedAt = now,
                    CreatedAt = now,
                });
            }
        }

        db.UnmappedAttendances.RemoveRange(unmapped.Attendances);
        db.UnmappedMembers.Remove(unmapped);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await auditLog.LogAsync(AdminAction.MemberUpdate, "UnmappedMember", unmappedMemberId.ToString(),
            $"미매핑 부원 '{unmapped.Label}' → {member.Name} 연동(출석 {unmapped.Attendances.Count}건 이관)");
    }
}
