using VolleyballClub.Application.Dtos;
using VolleyballClub.Application.Exceptions;
using Xunit;

namespace VolleyballClub.Tests;

/// <summary>관리자에 의한 부원 비밀번호 재설정.</summary>
public class MemberPasswordResetTests
{
    [Fact]
    public async Task Admin_UpdateWithNewPassword_SetsPassword()
    {
        await using var host = new ServiceHost();
        var admin = await host.SeedUserAsync("김회장", "admin");
        host.AsAdmin(admin);
        var member = await host.Member.AddAsync(new MemberCreateRequest(
            "202512001", "김민수", "체육학과", "OldPass1!"));

        await host.Member.UpdateAsync(member.UserId, new MemberUpdateRequest(
            "김민수", "체육학과", NewPassword: "NewPass9!"));

        var user = await host.Users.FindByIdAsync(member.UserId);
        Assert.NotNull(user);
        Assert.True(await host.Users.CheckPasswordAsync(user, "NewPass9!"));
        Assert.False(await host.Users.CheckPasswordAsync(user, "OldPass1!"));
    }

    [Fact]
    public async Task Admin_UpdateWithoutPassword_KeepsPassword()
    {
        await using var host = new ServiceHost();
        var admin = await host.SeedUserAsync("김회장", "admin");
        host.AsAdmin(admin);
        var member = await host.Member.AddAsync(new MemberCreateRequest(
            "202512002", "박지훈", "체육학과", "KeepPass1!"));

        await host.Member.UpdateAsync(member.UserId, new MemberUpdateRequest("박지훈", "체육학과"));

        var user = await host.Users.FindByIdAsync(member.UserId);
        Assert.NotNull(user);
        Assert.True(await host.Users.CheckPasswordAsync(user, "KeepPass1!"));
    }

    [Fact]
    public async Task Admin_UpdateWithShortPassword_ThrowsRuleViolation()
    {
        await using var host = new ServiceHost();
        var admin = await host.SeedUserAsync("김회장", "admin");
        host.AsAdmin(admin);
        var member = await host.Member.AddAsync(new MemberCreateRequest(
            "202512003", "이수현", "체육학과", "OldPass1!"));

        var ex = await Assert.ThrowsAsync<RuleViolationException>(() => host.Member.UpdateAsync(
            member.UserId, new MemberUpdateRequest("이수현", "체육학과", NewPassword: "abc")));
        Assert.Contains("4자", ex.Message);
    }
}
