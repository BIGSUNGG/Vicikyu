using VolleyballClub.Application.Dtos;
using VolleyballClub.Client.Components.Teams;

namespace VolleyballClub.Tests;

/// <summary>팀 뽑기 '오늘 출석' 명단 정렬 규칙 — 기본 이름 가나다순(게스트 혼합), 학번 정렬 시 게스트는 맨 아래.</summary>
public class ParticipantOrderTests
{
    private static ParticipantDto Member(string name, string studentNumber) =>
        new($"u:{studentNumber}", studentNumber, null, name, "게임콘텐츠스쿨", false, true, studentNumber);

    private static ParticipantDto Guest(string name, int id) =>
        new($"g:{id}", null, id, name, "게스트", true, true);

    [Fact]
    public void NameSort_MixesGuestsWithMembersInGanadaOrder()
    {
        var list = new List<ParticipantDto> { Member("하민준", "202512003"), Guest("가손님", 1), Member("강하늘", "202512001") };

        var sorted = ParticipantOrder.Sort(list, ParticipantSortField.Name, ascending: true);

        Assert.Equal(["가손님", "강하늘", "하민준"], sorted.Select(p => p.Name));
    }

    [Fact]
    public void NameSort_Descending_ReversesEveryone()
    {
        var list = new List<ParticipantDto> { Member("하민준", "202512003"), Guest("가손님", 1), Member("강하늘", "202512001") };

        var sorted = ParticipantOrder.Sort(list, ParticipantSortField.Name, ascending: false);

        Assert.Equal(["하민준", "강하늘", "가손님"], sorted.Select(p => p.Name));
    }

    [Fact]
    public void StudentNumberSort_MembersByNumber_GuestsLastInOriginalOrder()
    {
        var list = new List<ParticipantDto>
        {
            Member("나둘째", "202512002"), Guest("게스트B", 2), Member("가첫째", "202512001"), Guest("게스트A", 1),
        };

        var sorted = ParticipantOrder.Sort(list, ParticipantSortField.StudentNumber, ascending: true);

        Assert.Equal(["가첫째", "나둘째", "게스트B", "게스트A"], sorted.Select(p => p.Name));
    }

    [Fact]
    public void StudentNumberSort_Descending_MembersReversed_GuestsStillLast()
    {
        var list = new List<ParticipantDto>
        {
            Member("나둘째", "202512002"), Guest("게스트B", 2), Member("가첫째", "202512001"), Guest("게스트A", 1),
        };

        var sorted = ParticipantOrder.Sort(list, ParticipantSortField.StudentNumber, ascending: false);

        Assert.Equal(["나둘째", "가첫째", "게스트B", "게스트A"], sorted.Select(p => p.Name));
    }
}
