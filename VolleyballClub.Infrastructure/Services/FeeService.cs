using Microsoft.EntityFrameworkCore;
using VolleyballClub.Application.Abstractions;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Application.Exceptions;
using VolleyballClub.Domain;
using VolleyballClub.Domain.Entities;
using VolleyballClub.Domain.Enums;
using VolleyballClub.Infrastructure.Persistence;

namespace VolleyballClub.Infrastructure.Services;

public class FeeService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ICurrentUserService currentUser,
    IAuditLogService auditLog,
    TimeProvider timeProvider) : IFeeService
{
    public async Task<List<FeePeriodDto>> GetPeriodsAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.FeePeriods.AsNoTracking()
            .OrderByDescending(p => p.StartDate)
            .Select(p => new FeePeriodDto(p.Id, p.Name, p.StartDate, p.EndDate, p.FeeAmount))
            .ToListAsync();
    }

    public async Task<FeePeriodDto?> GetCurrentPeriodAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var today = KstClock.Today(timeProvider);
        var period = await db.FeePeriods.AsNoTracking()
            .Where(p => p.StartDate <= today && p.EndDate >= today)
            .OrderByDescending(p => p.StartDate)
            .FirstOrDefaultAsync();

        if (period is not null)
        {
            return new FeePeriodDto(period.Id, period.Name, period.StartDate, period.EndDate, period.FeeAmount);
        }

        // 진행 중인 기간이 없으면 가장 최근 기간
        var latest = await db.FeePeriods.AsNoTracking()
            .OrderByDescending(p => p.StartDate)
            .FirstOrDefaultAsync();
        return latest is null
            ? null
            : new FeePeriodDto(latest.Id, latest.Name, latest.StartDate, latest.EndDate, latest.FeeAmount);
    }

    public async Task<List<FeeStatusDto>> GetMyStatusesAsync(string userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await (
            from period in db.FeePeriods.AsNoTracking()
            join payment in db.FeePayments.AsNoTracking()
                .Where(p => p.UserId == userId)
                on period.Id equals payment.FeePeriodId into gp
            from payment in gp.DefaultIfEmpty()
            orderby period.StartDate descending
            select new FeeStatusDto(
                period.Id,
                period.Name,
                userId,
                "",
                "",
                "",
                payment != null && payment.IsPaid,
                payment != null ? payment.PaidAt : null,
                payment != null ? payment.Memo : null)
        ).ToListAsync();
    }

    public async Task<FeePeriodDto> CreatePeriodAsync(FeePeriodCreateRequest request)
    {
        await currentUser.RequireAdminAsync();
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new RuleViolationException("기간 이름을 입력해 주세요.");
        }

        if (request.StartDate > request.EndDate)
        {
            throw new RuleViolationException("시작일이 종료일보다 늦을 수 없습니다.");
        }

        if (request.FeeAmount < 0)
        {
            throw new RuleViolationException("회비 금액이 올바르지 않습니다.");
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        var period = new FeePeriod
        {
            Name = request.Name.Trim(),
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            FeeAmount = request.FeeAmount,
            CreatedAt = KstClock.NowUtc(timeProvider),
        };
        db.FeePeriods.Add(period);
        await db.SaveChangesAsync();
        await auditLog.LogAsync(AdminAction.FeePeriodCreate, "FeePeriod", period.Id.ToString(), $"회비 기간 '{period.Name}' 생성");
        return new FeePeriodDto(period.Id, period.Name, period.StartDate, period.EndDate, period.FeeAmount);
    }

    public async Task<List<FeeStatusDto>> GetPeriodStatusesAsync(int periodId, FeeFilter filter = FeeFilter.All, string? search = null)
    {
        await currentUser.RequireAdminAsync();
        await using var db = await dbFactory.CreateDbContextAsync();

        var query =
            from u in db.Users.AsNoTracking()
            join p in db.FeePayments.AsNoTracking()
                .Where(p => p.FeePeriodId == periodId)
                on u.Id equals p.UserId into gp
            from p in gp.DefaultIfEmpty()
            where u.IsActive
            select new FeeStatusDto(
                periodId,
                "",
                u.Id,
                u.Name,
                u.StudentNumber,
                u.Department,
                p != null && p.IsPaid,
                p != null ? p.PaidAt : null,
                p != null ? p.Memo : null);

        if (filter == FeeFilter.Paid)
        {
            query = query.Where(x => x.IsPaid);
        }
        else if (filter == FeeFilter.Unpaid)
        {
            query = query.Where(x => !x.IsPaid);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(x => x.Name.ToLower().Contains(term) || x.StudentNumber.ToLower().Contains(term));
        }

        var list = await query.ToListAsync();
        var periodName = await db.FeePeriods.AsNoTracking()
            .Where(p => p.Id == periodId)
            .Select(p => p.Name)
            .FirstOrDefaultAsync() ?? "";

        return list
            .Select(x => x with { PeriodName = periodName })
            .OrderByDescending(x => x.IsPaid)
            .ThenBy(x => x.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    public async Task SetPaidAsync(int periodId, string userId, bool isPaid, string? memo = null)
    {
        // 일반 부원의 회비 변경 서버 차단 — 화면 숨김과 별개로 여기서 막는다
        await currentUser.RequireAdminAsync();

        await using var db = await dbFactory.CreateDbContextAsync();
        _ = await db.FeePeriods.AsNoTracking().FirstOrDefaultAsync(p => p.Id == periodId)
            ?? throw new NotFoundException("회비 기간을 찾을 수 없습니다.");

        var member = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId)
            ?? throw new NotFoundException("부원을 찾을 수 없습니다.");

        var payment = await db.FeePayments.FirstOrDefaultAsync(p => p.FeePeriodId == periodId && p.UserId == userId);
        var now = KstClock.NowUtc(timeProvider);
        var adminId = await currentUser.RequireUserIdAsync();

        if (payment is null)
        {
            payment = new FeePayment
            {
                FeePeriodId = periodId,
                UserId = userId,
                IsPaid = isPaid,
                PaidAt = isPaid ? now : null,
                Memo = memo,
                UpdatedByUserId = adminId,
                UpdatedAt = now,
            };
            db.FeePayments.Add(payment);
        }
        else
        {
            payment.IsPaid = isPaid;
            payment.PaidAt = isPaid ? now : null;
            payment.Memo = memo ?? payment.Memo;
            payment.UpdatedByUserId = adminId;
            payment.UpdatedAt = now;
        }

        await db.SaveChangesAsync();
        await auditLog.LogAsync(AdminAction.FeeStatusChange, "FeePayment", $"{periodId}/{userId}",
            $"{member.Name} {(isPaid ? "납부 완료" : "미납 처리")}");
    }
}
