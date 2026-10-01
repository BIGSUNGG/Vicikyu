using VolleyballClub.Application.Dtos;

namespace VolleyballClub.Application.Abstractions;

public interface IFeeService
{
    Task<List<FeePeriodDto>> GetPeriodsAsync();

    Task<FeePeriodDto?> GetCurrentPeriodAsync();

    Task<List<FeeStatusDto>> GetMyStatusesAsync(string userId);

    Task<FeePeriodDto> CreatePeriodAsync(FeePeriodCreateRequest request);

    Task<List<FeeStatusDto>> GetPeriodStatusesAsync(int periodId, FeeFilter filter = FeeFilter.All, string? search = null);

    Task SetPaidAsync(int periodId, string userId, bool isPaid, string? memo = null);
}

public enum FeeFilter
{
    All = 0,
    Paid = 1,
    Unpaid = 2,
}
