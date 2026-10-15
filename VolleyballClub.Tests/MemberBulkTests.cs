using Microsoft.EntityFrameworkCore;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Application.Exceptions;

namespace VolleyballClub.Tests;

public sealed class MemberBulkTests
{
    private static MemberCreateRequest Row(string student, string role = "MEMBER") => new(student, "합성 부원", "", "test-only", role);

    [Fact]
    public async Task Validation_Rejects_Empty_Invalid_Preview_And_Db_Duplicates_Without_Writes()
    {
        await using var host = new ServiceHost();
        var admin = await host.SeedUserAsync("테스트 관리자", "admin");
        host.AsAdmin(admin);
        var invalid = new IReadOnlyList<MemberCreateRequest>[]
        {
            [], [Row("a") with { Name = " " }], [Row("a") with { Password = "123" }],
            [Row("a") with { Password = "    " }],
            [Row("a", "ROOT")], [Row("a"), Row(" A ")], [Row("new"), Row("admin")],
            [Row("a") with { Department = null! }], [null!], null!
        };
        foreach (var rows in invalid)
        {
            var result = await host.Member.AddBulkAsync(new(rows));
            Assert.Empty(result.Created);
            Assert.NotEmpty(result.Errors);
        }
        await using var db = host.Db.CreateContext();
        Assert.Equal(1, await db.Users.CountAsync());
        Assert.Empty(await db.UserRoles.ToListAsync());
        Assert.Empty(await db.AuditLogs.ToListAsync());
        host.AsMember(admin);
        await Assert.ThrowsAsync<ForbiddenException>(() => host.Member.AddBulkAsync(new([Row("b")])));
    }

    [Fact]
    public async Task Valid_Rows_Create_Hashed_Passwords_Roles_And_Audits()
    {
        await using var host = new ServiceHost();
        var admin = await host.SeedUserAsync("테스트 관리자", "admin");
        host.AsAdmin(admin);
        var result = await host.Member.AddBulkAsync(new([Row(" test-short "), Row("209900001", "ADMIN")]));
        Assert.Empty(result.Errors);
        Assert.Equal(2, result.Created.Count);
        var user = (await host.Users.FindByNameAsync("test-short"))!;
        Assert.True(await host.Users.CheckPasswordAsync(user, "test-only"));
        Assert.NotEqual("test-only", user.PasswordHash);
        Assert.Equal("", user.Department);
        Assert.Equal(["MEMBER"], await host.Users.GetRolesAsync(user));
        await using var db = host.Db.CreateContext();
        Assert.Equal(2, await db.UserRoles.CountAsync());
        Assert.Equal(2, await db.AuditLogs.CountAsync());
        Assert.DoesNotContain(await db.AuditLogs.ToListAsync(), a => a.Details.Contains("test-only"));
    }

    [Theory]
    [InlineData("audit")]
    [InlineData("role")]
    [InlineData("duplicate-race")]
    public async Task Mid_Write_Failure_Rolls_Back_Users_Roles_And_Audits(string failure)
    {
        await using var host = new ServiceHost();
        var admin = await host.SeedUserAsync("테스트 관리자", "admin");
        host.AsAdmin(admin);
        await using var db = host.Db.CreateContext();
        var trigger = failure switch
        {
            "audit" => "CREATE TRIGGER fail_bulk BEFORE INSERT ON AuditLogs WHEN (SELECT COUNT(*) FROM AuditLogs) = 1 BEGIN SELECT RAISE(ABORT, 'synthetic audit failure'); END;",
            "role" => "CREATE TRIGGER fail_bulk BEFORE INSERT ON AspNetUserRoles WHEN (SELECT COUNT(*) FROM AspNetUserRoles) = 1 BEGIN SELECT RAISE(ABORT, 'synthetic role failure'); END;",
            // Simulate an insert winning after prevalidation but before the second create.
            _ => "CREATE TRIGGER fail_bulk BEFORE INSERT ON AspNetUsers WHEN NEW.StudentNumber = 'second' BEGIN INSERT INTO AspNetUsers (Id, UserName, NormalizedUserName, StudentNumber, Name, Department, Status, JoinedAt, CreatedAt, UpdatedAt, EmailConfirmed, PhoneNumberConfirmed, TwoFactorEnabled, LockoutEnabled, AccessFailedCount) VALUES ('race-id', 'second', 'SECOND', 'second', 'synthetic', '', 0, '2026-01-01', '2026-01-01', '2026-01-01', 0, 0, 0, 0, 0); END;"
        };
        await db.Database.ExecuteSqlRawAsync(trigger);
        var result = await host.Member.AddBulkAsync(new([Row("first"), Row("second")]));
        Assert.Empty(result.Created);
        Assert.Contains(result.Errors, e => e.Row == 2);
        Assert.Equal(1, await db.Users.CountAsync());
        Assert.Empty(await db.UserRoles.ToListAsync());
        Assert.Empty(await db.AuditLogs.ToListAsync());
        Assert.Equal("admin", (await db.Users.SingleAsync()).StudentNumber);
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_bulk;");
        // Failure must not poison tracked Identity entries for a subsequent attempt.
        Assert.Empty((await host.Member.AddBulkAsync(new([Row("first"), Row("second")]))).Errors);
    }
}
