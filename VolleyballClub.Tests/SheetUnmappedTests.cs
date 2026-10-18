using VolleyballClub.Application.Abstractions;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Domain.Entities;
using VolleyballClub.Domain.Enums;
using VolleyballClub.Infrastructure.Services;
using Xunit;

namespace VolleyballClub.Tests;

/// <summary>부원 관리 페이지 미연결 경고 — 현재 학기 탭 기준 미지정 부원 판정(시트 읽기 없이 DB만 조회).</summary>
public class SheetUnmappedTests
{
    /// <summary>GetUnmappedUserIdsAsync는 시트를 읽으면 안 된다는 계약을 방어 — 어떤 호출도 즉시 실패.</summary>
    private sealed class ThrowingSheetClient : ISheetClient
    {
        public Task<IReadOnlyList<SheetTab>> GetTabsAsync(
            string spreadsheetId, IEnumerable<string>? tabNames = null, CancellationToken ct = default) =>
            throw new InvalidOperationException("미연경 판정은 시트를 읽지 않아야 한다(DB 전용)");

        public Task WriteColumnAsync(
            string spreadsheetId, string tabName, int columnIndex, SheetColumn column, CancellationToken ct = default) =>
            throw new InvalidOperationException();

        public Task InsertColumnAsync(
            string spreadsheetId, string tabName, int columnIndex, SheetColumn column, CancellationToken ct = default) =>
            throw new InvalidOperationException();

        public Task DeleteColumnAsync(
            string spreadsheetId, string tabName, int columnIndex, CancellationToken ct = default) =>
            throw new InvalidOperationException();
    }

    private static SheetMemberMappingService NewService(ServiceHost host) =>
        new(host.Db.Factory, new ThrowingSheetClient(), host.FormatResolver, host.Current, host.Audit, host.Time);

    private static async Task<int> SeedAttendanceSheetAsync(ServiceHost host)
    {
        await using var db = host.Db.CreateContext();
        var sheet = new SheetDefinition
        {
            Name = "배구부 출석부",
            SpreadsheetId = "sheet-1",
            Kind = SheetKind.Attendance,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.SheetDefinitions.Add(sheet);
        await db.SaveChangesAsync();
        return sheet.Id;
    }

    private static async Task SeedMappingAsync(ServiceHost host, int sheetId, string tabName, string userId, string rowLabel)
    {
        await using var db = host.Db.CreateContext();
        db.SheetMemberMappings.Add(new SheetMemberMapping
        {
            SheetDefinitionId = sheetId,
            TabName = tabName,
            RowLabel = rowLabel,
            UserId = userId,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task 미연결조회_현재학기탭_매핑없는_부원만_포함한다()
    {
        // 기본 시각 2026-10-01 → 전략상 현재 학기 탭은 '2026-2'
        var host = new ServiceHost(formatResolver: new SheetFormatResolver(SemesterTabFormat.All())).AsAdmin();
        var mapped = await host.SeedUserAsync("김연결", "1001");
        var unmappedUser = await host.SeedUserAsync("이미연", "1002");
        var wrongTab = await host.SeedUserAsync("박옛탭", "1003");
        var sheetId = await SeedAttendanceSheetAsync(host);
        await SeedMappingAsync(host, sheetId, "2026-2", mapped, "김연결 26 체육");
        await SeedMappingAsync(host, sheetId, "2026-1", wrongTab, "박옛탭 25 체육"); // 과거 탭 매핑은 현재 탭 판정과 무관

        var result = await NewService(host).GetUnmappedUserIdsAsync();

        Assert.Contains(unmappedUser, result);
        Assert.Contains(wrongTab, result);
        Assert.DoesNotContain(mapped, result);
    }

    [Fact]
    public async Task 미연결조회_비활성_부원도_포함한다()
    {
        var host = new ServiceHost(formatResolver: new SheetFormatResolver(SemesterTabFormat.All())).AsAdmin();
        var inactive = await host.SeedUserAsync("최비활", "2001", MemberStatus.Withdrawn);
        await SeedAttendanceSheetAsync(host);

        var result = await NewService(host).GetUnmappedUserIdsAsync();

        Assert.Contains(inactive, result);
    }

    [Fact]
    public async Task 미연결조회_현재_학기가_없으면_빈_집합이다()
    {
        // 전략 없음(방학 등) → 아무도 경고하지 않는다
        var host = new ServiceHost().AsAdmin();
        await host.SeedUserAsync("김방학", "3001");
        await SeedAttendanceSheetAsync(host);

        var result = await NewService(host).GetUnmappedUserIdsAsync();

        Assert.Empty(result);
    }

    [Fact]
    public async Task 미연결조회_출석_시트가_없으면_빈_집합이다()
    {
        var host = new ServiceHost(formatResolver: new SheetFormatResolver(SemesterTabFormat.All())).AsAdmin();
        await host.SeedUserAsync("김시트없", "4001");

        var result = await NewService(host).GetUnmappedUserIdsAsync();

        Assert.Empty(result);
    }
}
