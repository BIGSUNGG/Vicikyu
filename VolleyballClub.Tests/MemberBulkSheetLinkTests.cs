using Microsoft.EntityFrameworkCore;
using VolleyballClub.Application.Abstractions;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Application.Exceptions;
using VolleyballClub.Domain.Entities;
using VolleyballClub.Domain.Enums;
using VolleyballClub.Infrastructure.Services;

namespace VolleyballClub.Tests;

/// <summary>부원 일괄 추가의 시트 연결 — 이름 기반 행 후보 조회와
/// "일괄 추가로 만든 계정 + 추천 시트 행 연결이 DB에 실제로 반영됨"을 검증한다.</summary>
public sealed class MemberBulkSheetLinkTests
{
    private sealed class BulkHost : IAsyncDisposable
    {
        public ServiceHost Svc { get; } = new();
        public SheetMemberMappingService Mapping { get; }
        public FakeSheetClient Sheets => Svc.ManagedSheets;
        public int SheetId { get; private set; }
        public string AdminId { get; private set; } = "";

        public BulkHost()
        {
            Mapping = new SheetMemberMappingService(Svc.Db.Factory, Sheets,
                new SheetFormatResolver(SemesterTabFormat.All()), Svc.Current, Svc.Audit, Svc.Time);
        }

        public async Task SeedAsync()
        {
            AdminId = await Svc.SeedUserAsync("테스트 관리자", "admin");
            await using var db = Svc.Db.CreateContext();
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

        public ValueTask DisposeAsync() => Svc.DisposeAsync();
    }

    [Fact]
    public async Task 이름별_후보는_추천을_표시하고_타인_연결_행은_제외한다()
    {
        await using var h = new BulkHost();
        await h.SeedAsync();
        h.SeedTab("2026-2", "홍길동 25 게임", "김기존 26 만화");
        var holder = await h.Svc.SeedUserAsync("김기존", "202600099");
        h.Svc.AsAdmin();
        await h.Mapping.SetMappingAsync(h.SheetId, "2026-2", holder, "김기존 26 만화");

        var options = await h.Mapping.GetBulkOptionsAsync(["홍길동", "김기존"]);

        var guest = options["홍길동"].Single(t => t.TabName == "2026-2");
        Assert.Null(guest.CurrentRowLabel);
        var option = Assert.Single(guest.Options);
        Assert.Equal("홍길동 25 게임", option.RowLabel);
        Assert.True(option.Recommended);
        // 김기존 조회: 자신의 행은 타인(자기 계정) 연결로 제외되고 추천도 없다 — 타 행은 비추천 후보로 남는다
        var existing = options["김기존"].Single(t => t.TabName == "2026-2");
        Assert.Equal(["홍길동 25 게임"], existing.Options.Select(o => o.RowLabel));
        Assert.DoesNotContain(existing.Options, o => o.Recommended);
    }

    [Fact]
    public async Task 빈_이름은_무시하고_중복_이름은_하나로_묶는다()
    {
        await using var h = new BulkHost();
        await h.SeedAsync();
        h.SeedTab("2026-2", "홍길동 25 게임", "김기존 26 만화");
        h.Svc.AsAdmin();

        var options = await h.Mapping.GetBulkOptionsAsync([" ", " 홍길동 ", "홍길동", ""]);

        Assert.Equal(["홍길동"], options.Keys);
    }

    [Fact]
    public async Task 행_후보_조회는_관리자_전용이다()
    {
        await using var h = new BulkHost();
        await h.SeedAsync();
        var member = await h.Svc.SeedUserAsync("김부원", "202600098");
        h.Svc.AsMember(member);

        await Assert.ThrowsAsync<ForbiddenException>(() => h.Mapping.GetBulkOptionsAsync(["홍길동"]));
    }

    [Fact]
    public async Task 일괄_추가로_만든_계정에_추천_행_연결이_DB에_실제로_반영된다()
    {
        await using var h = new BulkHost();
        await h.SeedAsync();
        h.SeedTab("2026-2", "홍길동 25 게임", "김기존 26 만화");
        h.Svc.AsAdmin();

        // 클라이언트 제출 흐름과 동일: 후보 조회 → ★ 하나면 자동 선택 → 계정 생성 → 연결 저장
        var options = await h.Mapping.GetBulkOptionsAsync(["홍길동"]);
        var tab = options["홍길동"].Single(t => t.TabName == "2026-2");
        var recommended = Assert.Single(tab.Options.Where(o => o.Recommended));
        var result = await h.Svc.Member.AddBulkAsync(new([new MemberCreateRequest("202600001", "홍길동", "게임학과", "test-only", "MEMBER")]));
        Assert.Empty(result.Errors);
        await h.Mapping.SetMappingAsync(h.SheetId, "2026-2", result.Created[0].UserId, recommended.RowLabel);

        await using var db = h.Svc.Db.CreateContext();
        var mapping = await db.SheetMemberMappings.AsNoTracking().SingleAsync();
        Assert.Equal(result.Created[0].UserId, mapping.UserId);
        Assert.Equal(h.SheetId, mapping.SheetDefinitionId);
        Assert.Equal("2026-2", mapping.TabName);
        Assert.Equal("홍길동 25 게임", mapping.RowLabel);
    }
}
