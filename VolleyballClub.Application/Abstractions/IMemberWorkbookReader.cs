using VolleyballClub.Application.Dtos;

namespace VolleyballClub.Application.Abstractions;

public interface IMemberWorkbookReader
{
    IReadOnlyList<MemberCreateRequest> Read(Stream stream, string role, string password);
}
