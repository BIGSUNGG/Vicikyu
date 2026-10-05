using VolleyballClub.Application.Dtos;
using VolleyballClub.Application.Exceptions;
using VolleyballClub.Domain.Enums;
using Xunit;

namespace VolleyballClub.Tests;

/// <summary>
/// 시드 명세 §50의 전체 동작 시나리오를 서비스 레이어에서 수행한다:
/// 활동 생성 → OTP 출석 → 중복 거부 → 관리자 직접 추가 → 출석 마감 → 게스트 →
/// 랜덤 팀 편성 → 팀원 이동 → 확정 → 부원 팀 확인 → 활동 종료 → 캘린더/랭킹 반영 → 감사 로그.
/// </summary>
public class EndToEndScenarioTests
{
    [Fact]
    public async Task FullDayScenario_Works()
    {
        await using var host = new ServiceHost();
        var admin = await host.SeedUserAsync("김회장", "admin");
        var m1 = await host.SeedUserAsync("김민수", "202512001");
        var m2 = await host.SeedUserAsync("박지훈", "202512002");
        var m3 = await host.SeedUserAsync("이정우", "202512003");
        var m4 = await host.SeedUserAsync("홍길동", "202512004");

        // 1-4. 관리자: 활동 생성 → OTP 생성 → 출석 시작
        host.AsAdmin(admin);
        var today = await host.Activity.GetOrCreateTodayAsync();
        Assert.Equal(ActivityStatus.Ready, today.Status);
        await host.Activity.GenerateOtpAsync();
        await host.Activity.StartAttendanceAsync();
        var otp = (await host.Activity.GetTodayForAdminAsync()).OtpCode;
        Assert.Matches("^\\d{4}$", otp);

        // 5-8. 부원 OTP 출석
        foreach (var memberId in new[] { m1, m2, m3 })
        {
            host.AsMember(memberId);
            await host.Attendance.CheckInWithOtpAsync(otp!);
        }

        // 중복 출석 거부
        host.AsMember(m1);
        await Assert.ThrowsAsync<ConflictException>(() => host.Attendance.CheckInWithOtpAsync(otp!));

        // 9-10. 관리자 출석자 목록 확인 + 미출석자 직접 추가
        host.AsAdmin(admin);
        var members = await host.Attendance.GetTodayMembersAsync();
        Assert.Equal(5, members.Count); // 부원 4명 + 관리자 1명
        Assert.Equal(3, members.Count(x => x.IsAttended));
        Assert.All(members.Take(3), x => Assert.True(x.IsAttended)); // 출석자 우선 정렬
        await host.Attendance.AdminAddAsync(m4);

        // 11. 출석 마감
        await host.Activity.CloseAttendanceAsync();
        Assert.Equal(ActivityStatus.AttendanceClosed, (await host.Activity.GetTodayForAdminAsync()).Status);

        // 13. 게스트 추가
        await host.Team.AddGuestAsync("홍친구");
        var participants = await host.Team.GetParticipantsAsync();
        Assert.Equal(5, participants.Participants.Count);
        Assert.Single(participants.Participants, p => p.IsGuest);

        // 14-16. 3개 팀 — 인원 [2,2,1] 합계 5명으로 랜덤 편성
        var guestId = participants.Participants.Single(p => p.IsGuest).GuestId!.Value;
        var teams = await host.Team.GenerateAsync(new TeamCreateRequest([m1, m2, m3, m4], [guestId], [2, 2, 1]));
        Assert.Equal(3, teams.Count);
        Assert.Equal(5, teams.Sum(t => t.Members.Count));

        // 18-19. 팀원 이동
        var source = teams[0];
        var mover = source.Members[0];
        await host.Team.MoveMemberAsync(mover.TeamMemberId, teams[1].TeamId);
        teams = await host.Team.GetTodayTeamsForAdminAsync();
        Assert.Equal(3, teams[1].Members.Count); // B팀 2명 + 이동 1명
        Assert.Equal(5, teams.Sum(t => t.Members.Count)); // 이동 후에도 총 인원 불변
        Assert.Contains(teams[1].Members, m => m.TeamMemberId == mover.TeamMemberId);
        Assert.DoesNotContain(teams[0].Members, m => m.TeamMemberId == mover.TeamMemberId);

        // 20. 팀 확정
        await host.Team.ConfirmAsync();
        Assert.Equal(ActivityStatus.TeamCreated, (await host.Activity.GetTodayForAdminAsync()).Status);

        // 21. 일반 부원이 홈에서 자신의 팀을 확인
        host.AsMember(m2);
        var memberTeams = await host.Team.GetTodayTeamsAsync();
        Assert.Equal(3, memberTeams.Count);
        var myToday = await host.Activity.GetMyTodayAsync(m2);
        Assert.True(myToday.IsAttended);

        // 22. 활동 종료
        host.AsAdmin(admin);
        await host.Activity.CloseActivityAsync();
        Assert.Equal(ActivityStatus.ActivityClosed, (await host.Activity.GetTodayForAdminAsync()).Status);

        // 종료 후 수정 불가
        await Assert.ThrowsAsync<ConflictException>(() => host.Attendance.AdminAddAsync(admin));
        await Assert.ThrowsAsync<ConflictException>(
            () => host.Team.GenerateAsync(new TeamCreateRequest([m1, m2], [], [1, 1])));

        // 재오픈 가능
        await host.Activity.ReopenActivityAsync();
        Assert.Equal(ActivityStatus.TeamCreated, (await host.Activity.GetTodayForAdminAsync()).Status);

        // 23-24. 개인 캘린더·랭킹 반영
        host.AsMember(m2);
        var calendar = await host.Attendance.GetMyCalendarAsync(m2, 2026, 10);
        Assert.Equal(1, calendar.MonthCount);
        var attendedDay = calendar.Days.Single(d => d.IsAttended);
        Assert.NotEmpty(attendedDay.Teams);

        var stats = await host.Attendance.GetMySemesterStatsAsync(m2);
        Assert.Equal(1, stats.MyAttendances);
        Assert.Equal(100, stats.AttendanceRatePercent); // 개최 1회 중 1회 출석

        var ranking = await host.Attendance.GetRankingAsync(RankingPeriod.All);
        Assert.Equal(4, ranking.Count(x => x.Count == 1));

        // 감사 로그에 관리자 작업이 기록되었다
        host.AsAdmin(admin);
        var logs = await host.Audit.GetRecentAsync(50);
        Assert.Contains(logs, l => l.Action == AdminAction.ActivityCreate);
        Assert.Contains(logs, l => l.Action == AdminAction.AttendanceAdd);
        Assert.Contains(logs, l => l.Action == AdminAction.TeamConfirm);
        Assert.Contains(logs, l => l.Action == AdminAction.ActivityReopen);
    }
}
