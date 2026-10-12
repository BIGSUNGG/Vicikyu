using Microsoft.EntityFrameworkCore;
using VolleyballClub.Application.Abstractions;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Application.Exceptions;
using VolleyballClub.Domain;
using VolleyballClub.Domain.Entities;
using VolleyballClub.Domain.Enums;
using VolleyballClub.Infrastructure.Persistence;

namespace VolleyballClub.Infrastructure.Services;

public class SemesterService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ICurrentUserService currentUser,
    IAuditLogService auditLog,
    TimeProvider timeProvider) : ISemesterService
{
    public async Task<List<SemesterDto>> GetSemestersAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var today = KstClock.Today(timeProvider);
        // 진행 학기가 없는 날짜(방학)에는 가장 최근 학기를 '현재'로 표시한다
        var currentId = (await SemesterPeriodResolver.GetCurrentAsync(db, today))?.Id;
        return await db.SemesterPeriods.AsNoTracking()
            .OrderByDescending(p => p.StartDate)
            .Select(p => new SemesterDto(p.Id, p.Name, p.StartDate, p.EndDate, p.Id == currentId))
            .ToListAsync();
    }

    public async Task<SemesterDto> CreateAsync(SemesterSaveRequest request)
    {
        await currentUser.RequireAdminAsync();
        Validate(request);

        await using var db = await dbFactory.CreateDbContextAsync();
        var period = new SemesterPeriod
        {
            Name = request.Name.Trim(),
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            CreatedAt = KstClock.NowUtc(timeProvider),
        };
        db.SemesterPeriods.Add(period);
        await db.SaveChangesAsync();
        await auditLog.LogAsync(AdminAction.SemesterCreate, "SemesterPeriod", period.Id.ToString(), $"학기 '{period.Name}' 생성");
        return await ToDtoAsync(db, period);
    }

    public async Task<SemesterDto> UpdateAsync(int semesterId, SemesterSaveRequest request)
    {
        await currentUser.RequireAdminAsync();
        Validate(request);

        await using var db = await dbFactory.CreateDbContextAsync();
        var period = await db.SemesterPeriods.FirstOrDefaultAsync(p => p.Id == semesterId)
            ?? throw new NotFoundException("학기를 찾을 수 없습니다.");

        period.Name = request.Name.Trim();
        period.StartDate = request.StartDate;
        period.EndDate = request.EndDate;
        await db.SaveChangesAsync();
        await auditLog.LogAsync(AdminAction.SemesterUpdate, "SemesterPeriod", period.Id.ToString(), $"학기 '{period.Name}' 수정");
        return await ToDtoAsync(db, period);
    }

    public async Task DeleteAsync(int semesterId)
    {
        await currentUser.RequireAdminAsync();

        await using var db = await dbFactory.CreateDbContextAsync();
        var period = await db.SemesterPeriods.FirstOrDefaultAsync(p => p.Id == semesterId)
            ?? throw new NotFoundException("학기를 찾을 수 없습니다.");

        db.SemesterPeriods.Remove(period);
        await db.SaveChangesAsync();
        await auditLog.LogAsync(AdminAction.SemesterDelete, "SemesterPeriod", period.Id.ToString(), $"학기 '{period.Name}' 삭제");
    }

    /// <summary>IsCurrent는 GetSemestersAsync와 동일 규칙(오늘 포함 → 최근 학기)로 판정한다.</summary>
    private async Task<SemesterDto> ToDtoAsync(ApplicationDbContext db, SemesterPeriod period)
    {
        var today = KstClock.Today(timeProvider);
        var isCurrent = (await SemesterPeriodResolver.GetCurrentAsync(db, today))?.Id == period.Id;
        return new SemesterDto(period.Id, period.Name, period.StartDate, period.EndDate, isCurrent);
    }

    private static void Validate(SemesterSaveRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new RuleViolationException("학기 이름을 입력해 주세요.");
        }

        if (request.StartDate > request.EndDate)
        {
            throw new RuleViolationException("시작일이 종료일보다 늦을 수 없습니다.");
        }
    }
}
