using Microsoft.EntityFrameworkCore;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Application.Exceptions;
using Xunit;

namespace VolleyballClub.Tests;

/// <summary>MEMBER가 관리자 서비스를 직접 호출하면 서버에서 거부되는지 — 화면 숨김과 별개의 방어선.</summary>
public class MemberAdminAccessTests
{
    [Fact]
    public async Task Member_CannotRunAdminOperations()
    {
        await using var host = new ServiceHost();
        var admin = await host.SeedUserAsync("김회장", "admin");
        var member = await host.SeedUserAsync("김민수", "202512001");

        host.AsAdmin(admin);
        await host.Activity.GetOrCreateTodayAsync();

        // 활동 제어 전부 거부
        host.AsMember(member);
        await Assert.ThrowsAsync<ForbiddenException>(() => host.Activity.GetOrCreateTodayAsync());
        await Assert.ThrowsAsync<ForbiddenException>(() => host.Activity.GenerateOtpAsync());
        await Assert.ThrowsAsync<ForbiddenException>(() => host.Activity.StartAttendanceAsync());
        await Assert.ThrowsAsync<ForbiddenException>(() => host.Activity.CloseAttendanceAsync());
        await Assert.ThrowsAsync<ForbiddenException>(() => host.Activity.CloseActivityAsync());

        // 관리자 직접 출석 거부
        await Assert.ThrowsAsync<ForbiddenException>(() => host.Attendance.AdminAddAsync(member));
        await Assert.ThrowsAsync<ForbiddenException>(() => host.Attendance.AdminCancelAsync(member, true));
        await Assert.ThrowsAsync<ForbiddenException>(() => host.Attendance.GetTodayMembersAsync());

        // 팀 편성/게스트/확정 거부 (검증 로직 이전에 권한 확인이 우선한다)
        await Assert.ThrowsAsync<ForbiddenException>(
            () => host.Team.GenerateAsync(new TeamCreateRequest([member], [], [1, 1])));
        await Assert.ThrowsAsync<ForbiddenException>(() => host.Team.AddGuestAsync("홍친구"));
        await Assert.ThrowsAsync<ForbiddenException>(() => host.Team.ConfirmAsync());
        await Assert.ThrowsAsync<ForbiddenException>(() => host.Team.GetTodayTeamsForAdminAsync());
        await Assert.ThrowsAsync<ForbiddenException>(() => host.Team.GetParticipantsAsync());

        // 회비/부원 관리 거부
        await Assert.ThrowsAsync<ForbiddenException>(() => host.Fee.CreatePeriodAsync(
            new FeePeriodCreateRequest("테스트학기", new DateOnly(2026, 9, 1), new DateOnly(2026, 12, 31), 1000)));
        await Assert.ThrowsAsync<ForbiddenException>(() => host.Fee.SetPaidAsync(1, member, isPaid: true));
        await Assert.ThrowsAsync<ForbiddenException>(() => host.Member.GetListAsync());
        await Assert.ThrowsAsync<ForbiddenException>(
            () => host.Member.AddAsync(new MemberCreateRequest("202512099", "가짜부원", "학과", "Password123!")));

        // OTP 코드가 부원용 조회 DTO에 존재하지 않는다(구조 자체에 필드가 없음)
        var myToday = await host.Activity.GetMyTodayAsync(member);
        Assert.Null(myToday.GetType().GetProperty("OtpCode"));
    }

    [Fact]
    public async Task Admin_MemberOperations_Work()
    {
        await using var host = new ServiceHost();
        var admin = await host.SeedUserAsync("김회장", "admin");
        host.AsAdmin(admin);

        var created = await host.Member.AddAsync(new MemberCreateRequest("202512099", "새부원", "컴퓨터공학과", "Password123!"));
        Assert.Equal("새부원", created.Name);

        // 동일 학번 등록 거부
        await Assert.ThrowsAsync<Application.Exceptions.ConflictException>(
            () => host.Member.AddAsync(new MemberCreateRequest("202512099", "또다른부원", "학과", "Password123!")));

        var list = await host.Member.GetListAsync();
        Assert.Contains(list, m => m.StudentNumber == "202512099");
    }
}
