using Microsoft.EntityFrameworkCore;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Application.Exceptions;
using VolleyballClub.Domain.Entities;
using VolleyballClub.Domain.Enums;
using VolleyballClub.Infrastructure.Identity;
using VolleyballClub.Infrastructure.Services;

namespace VolleyballClub.Tests;

/// <summary>선택한 학기 탭의 부원 시트 행 연결·추가 — 다른 탭·기존 출석은 절대 바꾸지 않는다.</summary>
public sealed class SheetMemberRowLinkTests
{
    private sealed class Host : IAsyncDisposable
    {
        public TestDb Db { get; } = new();
        public FakeSheetClient Sheets { get; } = new();
        public SheetMemberMappingService Mapping { get; }
        public int SheetId { get; private set; }
        public string Admin { get; } = "admin-1";
        public string Member { get; } = "u1";

        public Host()
        {
            var time = new FixedTimeProvider(new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc));
            var current = new FakeCurrentUser(Admin, isAdmin: true);
            Mapping = new SheetMemberMappingService(Db.Factory, Sheets,
                new SheetFormatResolver(SemesterTabFormat.All()), current,
                new AuditLogService(Db.Factory, current, time), time);
        }

        public async Task SeedAsync()
        {
            await using var db = Db.CreateContext();
            db.Users.Add(new ApplicationUser { Id = Admin, Name = "관리자", StudentNumber = "admin", Status = MemberStatus.Active });
            db.Users.Add(new ApplicationUser { Id = Member, Name = "김신입", StudentNumber = "202600001", Status = MemberStatus.Active });
            var sheet = new SheetDefinition { Name = "출석부", SpreadsheetId = "ss", Kind = SheetKind.Attendance };
            db.SheetDefinitions.Add(sheet);
            await db.SaveChangesAsync();
            SheetId = sheet.Id;
        }

        /// <summary>신형 탭: 0행 상태, 1행 헤더, 2~3행 부원, 4행 총 참여자 수.</summary>
        public void SeedTab(string tabName, string firstMember, string secondMember) =>
            Sheets.Tabs.Add(new SheetTab(tabName, [
                ["활동 상태", "활동 완료"],
                ["부원", "26. 10. 01."],
                [firstMember, "출석"],
                [secondMember, "미출석"],
                ["총 참여자 수", "=COUNTIF(B3:B4,\"출석\")"]]));

        public async Task<int> AttendanceCountAsync()
        {
            await using var db = Db.CreateContext();
            return await db.Attendances.CountAsync();
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private static string Cell(ManagedSheetTab tab, int row, int column) =>
        tab.Rows.Count > row && tab.Rows[row].Count > column ? tab.Rows[row][column] : "";

    [Fact]
    public async Task 행_후보는_부원_구간만_보여준다_집계행_중복_타인_연결_제외()
    {
        await using var h = new Host();
        await h.SeedAsync();
        h.SeedTab("2026-2", "박유진 24 만화", "김신입 25 만화");
        await using (var db = h.Db.CreateContext())
        {
            db.SheetMemberMappings.Add(new SheetMemberMapping { SheetDefinitionId = h.SheetId, TabName = "2026-2", UserId = "other", RowLabel = "박유진 24 만화" });
            await db.SaveChangesAsync();
        }

        var tabs = await h.Mapping.GetForMemberAsync(h.Member);

        var options = tabs.Single(t => t.TabName == "2026-2").Options;
        Assert.Equal(["김신입 25 만화"], options.Select(o => o.RowLabel));
        Assert.True(options.Single().Recommended);
    }

    [Fact]
    public async Task 기존_행_연결은_선택_탭_매핑만_저장하고_동기화를_돌리지_않는다()
    {
        await using var h = new Host();
        await h.SeedAsync();
        h.SeedTab("2026-2", "박유진 24 만화", "김신입 25 만화");

        await h.Mapping.SetMappingAsync(h.SheetId, "2026-2", h.Member, "김신입 25 만화");

        await using var db = h.Db.CreateContext();
        Assert.Equal("김신입 25 만화", (await db.SheetMemberMappings.SingleAsync()).RowLabel);
        Assert.Equal(0, await db.Attendances.CountAsync());
        Assert.Empty(h.Sheets.ManagedWrites);
        var after = await h.Sheets.GetManagedTabAsync("ss", "2026-2");
        Assert.Equal("박유진 24 만화", after.Rows[2][0]);
    }

    [Fact]
    public async Task 새_행_추가는_총참여자_직전에_빈_행을_넣고_집계_범위를_한_칸_늘린다()
    {
        await using var h = new Host();
        await h.SeedAsync();
        h.SeedTab("2026-2", "박유진 24 만화", "김신입 25 만화");

        await h.Mapping.SetMappingAsync(h.SheetId, "2026-2", h.Member, "김신입", addNewRow: true);

        var after = await h.Sheets.GetManagedTabAsync("ss", "2026-2");
        Assert.Equal(6, after.Rows.Count);
        Assert.Equal("김신입", after.Rows[4][0]);
        Assert.Equal("", Cell(after, 4, 1));
        Assert.Equal("총 참여자 수", after.Rows[5][0]);
        Assert.Equal("=COUNTIF(B3:B5,\"출석\")", after.Rows[5][1]);
        Assert.Equal("박유진 24 만화", after.Rows[2][0]);
        Assert.Equal("출석", after.Rows[2][1]);
        await using var db = h.Db.CreateContext();
        Assert.Equal("김신입", (await db.SheetMemberMappings.SingleAsync()).RowLabel);
        Assert.Empty(await db.SheetEditJournals.ToListAsync());
    }

    [Fact]
    public async Task 유실된_응답_재시도는_두_번째_행을_만들지_않는다()
    {
        await using var h = new Host();
        await h.SeedAsync();
        h.SeedTab("2026-2", "박유진 24 만화", "김신입 25 만화");

        await h.Mapping.SetMappingAsync(h.SheetId, "2026-2", h.Member, "김신입", addNewRow: true);
        await h.Mapping.SetMappingAsync(h.SheetId, "2026-2", h.Member, "김신입", addNewRow: true);

        var after = await h.Sheets.GetManagedTabAsync("ss", "2026-2");
        Assert.Equal(6, after.Rows.Count);
        await using var db = h.Db.CreateContext();
        Assert.Single(await db.SheetMemberMappings.ToListAsync());
    }

    [Fact]
    public async Task 쓰기_실패시_행도_매핑도_남기지_않는다()
    {
        await using var h = new Host();
        await h.SeedAsync();
        h.SeedTab("2026-2", "박유진 24 만화", "김신입 25 만화");
        h.Sheets.BeforeManagedApply = _ => throw new IOException("synthetic sheet outage");

        await Assert.ThrowsAsync<IOException>(() =>
            h.Mapping.SetMappingAsync(h.SheetId, "2026-2", h.Member, "김신입", addNewRow: true));

        var after = await h.Sheets.GetManagedTabAsync("ss", "2026-2");
        Assert.Equal(5, after.Rows.Count);
        await using var db = h.Db.CreateContext();
        Assert.Empty(await db.SheetMemberMappings.ToListAsync());
        Assert.Empty(await db.SheetEditJournals.ToListAsync());
    }

    [Theory]
    [InlineData("총 참여자 수")]
    [InlineData("탈퇴/퇴출자")]
    public async Task 집계_경계_이름은_부원_행으로_쓸_수_없다(string label)
    {
        await using var h = new Host();
        await h.SeedAsync();
        h.SeedTab("2026-2", "박유진 24 만화", "김신입 25 만화");

        await Assert.ThrowsAsync<RuleViolationException>(() =>
            h.Mapping.SetMappingAsync(h.SheetId, "2026-2", h.Member, label, addNewRow: true));
        Assert.Empty(h.Sheets.ManagedWrites);
    }

    [Fact]
    public async Task 다른_사람이_연결한_행이나_부원_구간_밖_행은_연결할_수_없다()
    {
        await using var h = new Host();
        await h.SeedAsync();
        h.SeedTab("2026-2", "박유진 24 만화", "김신입 25 만화");
        await using (var db = h.Db.CreateContext())
        {
            db.SheetMemberMappings.Add(new SheetMemberMapping { SheetDefinitionId = h.SheetId, TabName = "2026-2", UserId = "other", RowLabel = "김신입 25 만화" });
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<ConflictException>(() =>
            h.Mapping.SetMappingAsync(h.SheetId, "2026-2", h.Member, "김신입 25 만화"));
        await Assert.ThrowsAsync<RuleViolationException>(() =>
            h.Mapping.SetMappingAsync(h.SheetId, "2026-2", h.Member, "총 참여자 수"));
        await using var verify = h.Db.CreateContext();
        Assert.Empty(await verify.SheetMemberMappings.Where(m => m.UserId == h.Member).ToListAsync());
    }

    [Fact]
    public async Task 여러_학기_탭이_있어도_선택한_탭만_변경한다()
    {
        await using var h = new Host();
        await h.SeedAsync();
        h.SeedTab("2026-2", "박유진 24 만화", "김신입 25 만화");
        h.SeedTab("2026-1", "박유진 24 만화", "김신입 25 만화");

        await h.Mapping.SetMappingAsync(h.SheetId, "2026-2", h.Member, "김신입", addNewRow: true);

        var other = await h.Sheets.GetManagedTabAsync("ss", "2026-1");
        Assert.Equal(5, other.Rows.Count);
        Assert.Equal("총 참여자 수", other.Rows[4][0]);
        Assert.Equal("=COUNTIF(B3:B4,\"출석\")", other.Rows[4][1]);
        await using var db = h.Db.CreateContext();
        Assert.Equal("2026-2", (await db.SheetMemberMappings.SingleAsync()).TabName);
    }

    [Fact]
    public async Task 해제는_매핑만_지우고_시트_행은_그대로_둔다()
    {
        await using var h = new Host();
        await h.SeedAsync();
        h.SeedTab("2026-2", "박유진 24 만화", "김신입 25 만화");
        await h.Mapping.SetMappingAsync(h.SheetId, "2026-2", h.Member, "김신입 25 만화");

        await h.Mapping.SetMappingAsync(h.SheetId, "2026-2", h.Member, null);

        await using var db = h.Db.CreateContext();
        Assert.Empty(await db.SheetMemberMappings.ToListAsync());
        Assert.Equal(5, (await h.Sheets.GetManagedTabAsync("ss", "2026-2")).Rows.Count);
    }
}
