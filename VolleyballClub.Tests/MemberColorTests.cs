using Microsoft.EntityFrameworkCore;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Application.Exceptions;
using VolleyballClub.Domain;
using Xunit;

namespace VolleyballClub.Tests;

/// <summary>부원 색 규칙 — 팔레트 밖 색 저장 거부, 랜덤 기본 배정, 본인 색/비밀번호 변경.</summary>
public class MemberColorTests
{
    [Theory]
    [InlineData("#123456")]
    [InlineData("red")]
    [InlineData("")]
    public void MemberColors_Rejects_Values_Outside_Palette(string invalid)
    {
        Assert.False(MemberColors.Contains(invalid));
        foreach (var valid in MemberColors.Palette)
        {
            Assert.True(MemberColors.Contains(valid));
        }
    }

    [Fact]
    public async Task AddAsync_Assigns_Random_Palette_Color()
    {
        await using var host = new ServiceHost();
        host.AsAdmin(await host.SeedUserAsync("김회장", "admin"));

        var created = await host.Member.AddAsync(new MemberCreateRequest("202512099", "새부원", "컴퓨터공학과", "Password123!"));

        Assert.Contains(created.Color, MemberColors.Palette);
        await using var db = host.Db.CreateContext();
        Assert.Contains(
            await db.Users.Where(u => u.Id == created.UserId).Select(u => u.Color).SingleAsync(),
            MemberColors.Palette);
    }

    [Fact]
    public async Task UpdateMyColorAsync_Rejects_Outside_Palette_And_Saves_Valid()
    {
        await using var host = new ServiceHost();
        var member = await host.SeedUserAsync("김민수", "202512001");
        host.AsMember(member);

        await Assert.ThrowsAsync<RuleViolationException>(() => host.Member.UpdateMyColorAsync("#123456"));

        var chosen = MemberColors.Palette[3];
        await host.Member.UpdateMyColorAsync(chosen);
        await using var db = host.Db.CreateContext();
        Assert.Equal(chosen, await db.Users.Where(u => u.Id == member).Select(u => u.Color).SingleAsync());
    }

    [Fact]
    public async Task Admin_Can_Set_Member_Color_From_Palette_Only()
    {
        await using var host = new ServiceHost();
        var admin = await host.SeedUserAsync("김회장", "admin");
        var member = await host.SeedUserAsync("김민수", "202512001");
        host.AsAdmin(admin);

        await Assert.ThrowsAsync<RuleViolationException>(() => host.Member.UpdateAsync(member,
            new MemberUpdateRequest("김민수", "학과", Color: "#123456")));

        var chosen = MemberColors.Palette[5];
        await host.Member.UpdateAsync(member, new MemberUpdateRequest("김민수", "학과", Color: chosen));
        await using var db = host.Db.CreateContext();
        Assert.Equal(chosen, await db.Users.Where(u => u.Id == member).Select(u => u.Color).SingleAsync());
    }

    [Fact]
    public async Task ChangeMyPasswordAsync_Requires_Current_Password()
    {
        await using var host = new ServiceHost();
        host.AsAdmin(await host.SeedUserAsync("김회장", "admin"));
        var created = await host.Member.AddAsync(new MemberCreateRequest("202512099", "새부원", "학과", "Password123!"));
        host.AsMember(created.UserId);

        await Assert.ThrowsAsync<RuleViolationException>(
            () => host.Member.ChangeMyPasswordAsync(new PasswordChangeRequest("WrongPass1!", "NewPass1234!")));

        await Assert.ThrowsAsync<RuleViolationException>(
            () => host.Member.ChangeMyPasswordAsync(new PasswordChangeRequest("Password123!", "abc")));

        await host.Member.ChangeMyPasswordAsync(new PasswordChangeRequest("Password123!", "NewPass1234!"));

        // 이전 비밀번호로는 더 이상 변경할 수 없다 = 실제로 바뀌었다
        await Assert.ThrowsAsync<RuleViolationException>(
            () => host.Member.ChangeMyPasswordAsync(new PasswordChangeRequest("Password123!", "OtherPass12!")));
    }
}
