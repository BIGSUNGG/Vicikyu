using VolleyballClub.Application.Dtos;

namespace VolleyballClub.Client.Components.Teams;

public enum ParticipantSortField
{
    Name,
    StudentNumber,
}

/// <summary>팀 뽑기 '오늘 출석' 명단 정렬. 이름은 게스트를 포함해 가나다순으로 섞고,
/// 학번은 부원만 학번순으로 세우고 게스트는 방향과 무관하게 맨 아래에 원래 순서로 둔다.</summary>
public static class ParticipantOrder
{
    public static List<ParticipantDto> Sort(IEnumerable<ParticipantDto> participants, ParticipantSortField field, bool ascending)
    {
        if (field == ParticipantSortField.Name)
        {
            var byName = participants.OrderBy(p => p.Name, StringComparer.CurrentCulture);
            return (ascending ? byName : byName.Reverse()).ToList();
        }

        var members = participants.Where(p => !p.IsGuest)
            .OrderBy(p => p.StudentNumber, StringComparer.CurrentCulture)
            .ToList();
        if (!ascending)
        {
            members.Reverse();
        }

        members.AddRange(participants.Where(p => p.IsGuest));
        return members;
    }
}
