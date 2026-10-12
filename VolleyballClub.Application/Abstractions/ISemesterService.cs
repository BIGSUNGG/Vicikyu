using VolleyballClub.Application.Dtos;

namespace VolleyballClub.Application.Abstractions;

public interface ISemesterService
{
    Task<List<SemesterDto>> GetSemestersAsync();

    Task<SemesterDto> CreateAsync(SemesterSaveRequest request);

    Task<SemesterDto> UpdateAsync(int semesterId, SemesterSaveRequest request);

    Task DeleteAsync(int semesterId);
}
