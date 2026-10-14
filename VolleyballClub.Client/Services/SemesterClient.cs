using VolleyballClub.Application.Abstractions;
using VolleyballClub.Application.Dtos;
namespace VolleyballClub.Client.Services;
public sealed class SemesterClient(ApiHttp api)
{
    public Task<List<SemesterDto>> GetSemestersAsync() => api.GetAsync<List<SemesterDto>>($"api/admin/semesters");
    public Task<SemesterDto> CreateAsync(SemesterSaveRequest request) => api.PostAsync<SemesterDto>($"api/admin/semesters", request);
    public Task<SemesterDto> UpdateAsync(int semesterId, SemesterSaveRequest request) => api.PutAsync<SemesterDto>($"api/admin/semesters/{semesterId}", request);
    public Task DeleteAsync(int semesterId) => api.DeleteAsync($"api/admin/semesters/{semesterId}");
}
