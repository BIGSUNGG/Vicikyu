using VolleyballClub.Application.Dtos;

namespace VolleyballClub.Application.Abstractions;

/// <summary>xlsx 가져오기에서 매칭 실패한 부원 행의 임시 보관·계정 연동.</summary>
public interface IUnmappedMemberService
{
    Task<IReadOnlyList<UnmappedMemberDto>> GetListAsync(CancellationToken ct = default);

    /// <summary>미매핑 부원의 출석 기록을 실제 계정으로 이관하고 임시 항목을 삭제한다.</summary>
    Task LinkAsync(int unmappedMemberId, string userId, CancellationToken ct = default);
}
