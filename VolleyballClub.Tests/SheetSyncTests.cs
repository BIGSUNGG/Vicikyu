using Microsoft.EntityFrameworkCore;
using VolleyballClub.Application.Abstractions;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Domain.Entities;
using VolleyballClub.Domain.Enums;
using VolleyballClub.Infrastructure.Identity;
using VolleyballClub.Infrastructure.Services;

namespace VolleyballClub.Tests;

/// <summary>ISheetClient 스텁 — 탭 읽기/열 기록·삽입을 기록한다.</summary>
public sealed class FakeSheetClient : ISheetClient
{
    public List<SheetTab> Tabs { get; } = [];

    public List<(string TabName, bool Insert, int ColumnIndex, SheetColumn Column)> Writes { get; } = [];

    public Func<string, int, SheetColumn, Task>? WriteHandler { get; set; }

    public Func<string, int, SheetColumn, Task>? InsertHandler { get; set; }

    public Task<IReadOnlyList<SheetTab>> GetTabsAsync(
        string spreadsheetId, IEnumerable<string>? tabNames = null, CancellationToken ct = default)
    {
        IReadOnlyList<SheetTab> result = tabNames is null ? Tabs : Tabs.Where(t => tabNames.Contains(t.Name)).ToList();
        return Task.FromResult(result);
    }

    public async Task WriteColumnAsync(
        string spreadsheetId, string tabName, int columnIndex, SheetColumn column, CancellationToken ct = default)
    {
        if (WriteHandler is not null)
        {
            await WriteHandler(tabName, columnIndex, column);
            return;
        }

        Writes.Add((tabName, false, columnIndex, column));
    }

    public async Task InsertColumnAsync(
        string spreadsheetId, string tabName, int columnIndex, SheetColumn column, CancellationToken ct = default)
    {
        if (InsertHandler is not null)
        {
            await InsertHandler(tabName, columnIndex, column);
            return;
        }

        Writes.Add((tabName, true, columnIndex, column));
    }
}

/// <summary>학기 서식 스텁 — 파싱 결과와 열 생성을 테스트에서 주입한다.</summary>
public sealed class FakeSemesterFormat(string tabName = "2026-1") : ISemesterSheetFormat
{
    public DateOnly From { get; init; } = new(2026, 3, 1);

    public DateOnly To { get; init; } = new(2026, 6, 30);

    public SheetSemesterData Data { get; init; } = new([]);

    public Func<DateOnly, IReadOnlySet<string>, SheetWriteTarget>? BuildHandler { get; set; }

    public string TabName => tabName;

    public bool CoversDate(DateOnly date) => date >= From && date <= To;

    public SheetSemesterData Parse(SheetTab tab) => Data;

    public SheetWriteTarget BuildColumn(SheetTab tab, DateOnly activityDate, IReadOnlySet<string> attendedRowLabels) =>
        BuildHandler?.Invoke(activityDate, attendedRowLabels)
        ?? new SheetWriteTarget(false, 2, new SheetColumn(2, [activityDate.ToString("M/d")]));
}

/// <summary>SheetSyncService 테스트 호스트 — 시트 클라이언트·전략·시트 정의를 직접 조립한다.</summary>
public sealed class SheetSyncHost : IAsyncDisposable
{
    public TestDb Db { get; } = new();

    public FakeSheetClient Client { get; } = new();

    public SheetSyncService Sync { get; }

    public int SheetId { get; private set; }

    public SheetSyncHost(FakeSemesterFormat? format = null)
    {
        Sync = new SheetSyncService(
            Db.Factory,
            Client,
            new SheetFormatResolver([format ?? new FakeSemesterFormat()]),
            new FixedTimeProvider(new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc)));
    }

    public async Task<int> SeedSheetAsync()
    {
        await using var db = Db.CreateContext();
        var now = DateTime.UtcNow;
        var sheet = new SheetDefinition
        {
            Name = "출석부",
            SpreadsheetId = "ss-test",
            Kind = SheetKind.Attendance,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.SheetDefinitions.Add(sheet);
        await db.SaveChangesAsync();
        SheetId = sheet.Id;
        return sheet.Id;
    }

    public async Task SeedMappingAsync(string tabName, string rowLabel, string userId)
    {
        await using var db = Db.CreateContext();
        db.SheetMemberMappings.Add(new SheetMemberMapping
        {
            SheetDefinitionId = SheetId,
            TabName = tabName,
            RowLabel = rowLabel,
            UserId = userId,
        });
        await db.SaveChangesAsync();
    }

    public ValueTask DisposeAsync() => Db.DisposeAsync();
}

public sealed class SheetSyncTests
{
    [Fact]
    public async Task SyncAsync_매핑된_라벨을_마감활동으로_가져온다()
    {
        await using var host = new SheetSyncHost(new FakeSemesterFormat
        {
            Data = new SheetSemesterData([
                new SheetActivityColumn(new DateOnly(2026, 3, 10), ["박유진 24 만화", "김하영 25 게임"]),
                new SheetActivityColumn(new DateOnly(2026, 3, 17), ["박유진 24 만화"]),
            ]),
        });
        var (user1, user2) = await SeedUsersAsync(host.Db);
        await host.SeedSheetAsync();
        await host.SeedMappingAsync("2026-1", "박유진 24 만화", user1);
        await host.SeedMappingAsync("2026-1", "김하영 25 게임", user2);
        host.Client.Tabs.Add(new SheetTab("2026-1", []));

        var report = await host.Sync.SyncAsync();

        await using var db = host.Db.CreateContext();
        var activities = await db.Activities.OrderBy(a => a.ActivityDate).ToListAsync();
        Assert.Equal(2, activities.Count);
        Assert.All(activities, a => Assert.Equal(ActivityStatus.ActivityClosed, a.Status));
        Assert.All(activities, a => Assert.NotNull(a.SheetExportedAt)); // 이미 시트에 있으므로 내보내기 대상 아님
        Assert.Equal(3, await db.Attendances.CountAsync(a => a.AttendanceType == AttendanceType.Sheet));
        Assert.Equal(2, report.ImportedActivities);
        Assert.Equal(3, report.ImportedAttendances);
    }

    [Fact]
    public async Task SyncAsync_재실행은_멱등이다()
    {
        await using var host = new SheetSyncHost(new FakeSemesterFormat
        {
            Data = new SheetSemesterData([new SheetActivityColumn(new DateOnly(2026, 3, 10), ["박유진"])]),
        });
        var (user1, _) = await SeedUsersAsync(host.Db);
        await host.SeedSheetAsync();
        await host.SeedMappingAsync("2026-1", "박유진", user1);
        host.Client.Tabs.Add(new SheetTab("2026-1", []));

        await host.Sync.SyncAsync();
        var second = await host.Sync.SyncAsync();

        await using var db = host.Db.CreateContext();
        Assert.Equal(0, second.ImportedActivities);
        Assert.Equal(1, await db.Activities.CountAsync());
        Assert.Equal(1, await db.Attendances.CountAsync());
    }

    [Fact]
    public async Task SyncAsync_미지정_라벨은_경고로_남긴다()
    {
        await using var host = new SheetSyncHost(new FakeSemesterFormat
        {
            Data = new SheetSemesterData([new SheetActivityColumn(new DateOnly(2026, 3, 10), ["연결안된사람"])]),
        });
        await host.SeedSheetAsync();
        host.Client.Tabs.Add(new SheetTab("2026-1", []));

        var report = await host.Sync.SyncAsync();

        Assert.Equal(1, report.SkippedUnmatchedRows);
        Assert.Contains(report.Warnings, w => w.Contains("미지정"));
        await using var db = host.Db.CreateContext();
        Assert.Equal(1, await db.Activities.CountAsync()); // 활동 자체는 가져온다(출석 0명)
        Assert.Equal(0, await db.Attendances.CountAsync());
    }

    [Fact]
    public async Task SyncAsync_전략없는_탭과_시트_미등록을_처리한다()
    {
        await using var host = new SheetSyncHost();
        await host.SeedSheetAsync();
        host.Client.Tabs.Add(new SheetTab("회비", []));

        var withUnknownTab = await host.Sync.SyncAsync();
        Assert.Contains(withUnknownTab.Warnings, w => w.Contains("전략 없음"));

        await using var db = host.Db.CreateContext();
        db.SheetDefinitions.Remove(await db.SheetDefinitions.FirstAsync());
        await db.SaveChangesAsync();

        var noSheet = await host.Sync.SyncAsync();
        Assert.Contains(noSheet.Warnings, w => w.Contains("등록된 출석 시트가 없습니다"));
    }

    [Fact]
    public async Task SyncAsync_미내보낸_마감활동을_열로_복구한다()
    {
        await using var host = new SheetSyncHost();
        var (user1, _) = await SeedUsersAsync(host.Db);
        await host.SeedSheetAsync();
        await host.SeedMappingAsync("2026-1", "박유진 24 만화", user1);
        await SeedClosedActivityAsync(host.Db, new DateOnly(2026, 3, 10), user1);
        host.Client.Tabs.Add(new SheetTab("2026-1", []));

        var report = await host.Sync.SyncAsync();

        Assert.Equal(1, report.ExportedColumns);
        Assert.Single(host.Client.Writes, w => w.TabName == "2026-1");
        await using var db = host.Db.CreateContext();
        Assert.True(await db.Activities.AllAsync(a => a.SheetExportedAt != null));
    }

    [Fact]
    public async Task ExportClosedActivityAsync_실패시_false이고_마커유지_이후_복구된다()
    {
        await using var host = new SheetSyncHost();
        var (user1, _) = await SeedUsersAsync(host.Db);
        await host.SeedSheetAsync();
        await host.SeedMappingAsync("2026-1", "박유진 24 만화", user1);
        var activity = await SeedClosedActivityAsync(host.Db, new DateOnly(2026, 3, 10), user1);
        host.Client.Tabs.Add(new SheetTab("2026-1", []));
        host.Client.WriteHandler = (_, _, _) => throw new InvalidOperationException("시트 쓰기 실패");

        Assert.False(await host.Sync.ExportClosedActivityAsync(activity.Id));
        await using (var db = host.Db.CreateContext())
        {
            Assert.Null(await db.Activities.Where(a => a.Id == activity.Id).Select(a => a.SheetExportedAt).FirstAsync());
        }

        host.Client.WriteHandler = null;
        Assert.True(await host.Sync.ExportClosedActivityAsync(activity.Id));
        Assert.Single(host.Client.Writes);
    }

    [Fact]
    public async Task ExportClosedActivityAsync_이미내보낸_활동은_true()
    {
        await using var host = new SheetSyncHost();
        var activity = await SeedClosedActivityAsync(host.Db, new DateOnly(2026, 3, 10));
        await using (var db = host.Db.CreateContext())
        {
            var tracked = await db.Activities.FirstAsync(a => a.Id == activity.Id);
            tracked.SheetExportedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        Assert.True(await host.Sync.ExportClosedActivityAsync(activity.Id));
        Assert.Empty(host.Client.Writes);
    }

    [Fact]
    public async Task 활동_마감이_시트_내보내기를_호출한다()
    {
        await using var host = new ServiceHost();
        var admin = await host.SeedUserAsync("김회장", "admin");
        await host.SeedActivityAsync(new DateOnly(2026, 10, 1));
        var activity = await host.GetTodayActivityAsync();

        await host.AsAdmin(admin).Activity.CloseActivityAsync();

        Assert.Contains(activity.Id, host.SheetSync.ExportedActivityIds);
    }

    [Fact]
    public async Task SyncAsync_매핑_추가_후_재실행하면_출석이_백필된다()
    {
        await using var host = new SheetSyncHost(new FakeSemesterFormat
        {
            Data = new SheetSemesterData([new SheetActivityColumn(new DateOnly(2026, 3, 10), ["박유진"])]),
        });
        var (user1, _) = await SeedUsersAsync(host.Db);
        await host.SeedSheetAsync();
        host.Client.Tabs.Add(new SheetTab("2026-1", []));

        var first = await host.Sync.SyncAsync();
        Assert.Equal(1, first.ImportedActivities);
        Assert.Equal(0, first.ImportedAttendances); // 매핑 없음 → 출석 0으로 활동만 생성

        await host.SeedMappingAsync("2026-1", "박유진", user1);
        var second = await host.Sync.SyncAsync();

        Assert.Equal(1, second.ImportedActivities); // 백필된 활동으로 집계
        Assert.Equal(1, second.ImportedAttendances);
        await using var db = host.Db.CreateContext();
        Assert.Equal(1, await db.Attendances.CountAsync(a => a.AttendanceType == AttendanceType.Sheet));

        var third = await host.Sync.SyncAsync();
        Assert.Equal(0, third.ImportedAttendances); // 이미 백필됨 — 멱등
    }

    [Fact]
    public async Task 다시_열기하면_시트_재기록_대상으로_초기화된다()
    {
        await using var host = new ServiceHost();
        var admin = await host.SeedUserAsync("김회장", "admin");
        await host.SeedActivityAsync(new DateOnly(2026, 10, 1), ActivityStatus.ActivityClosed);
        await using (var db = host.Db.CreateContext())
        {
            var activity = await db.Activities.FirstAsync();
            activity.SheetExportedAt = DateTime.UtcNow; // 이미 시트에 기록된 상태
            await db.SaveChangesAsync();
        }

        await host.AsAdmin(admin).Activity.ReopenActivityAsync();

        await using (var db = host.Db.CreateContext())
        {
            Assert.Null(await db.Activities.Select(a => a.SheetExportedAt).FirstAsync());
        }
    }

    [Fact]
    public async Task 설정_전체_조회에_서비스_계정_키가_노출되지_않는다()
    {
        await using var host = new ServiceHost();
        host.AsAdmin();
        await host.Settings.SetAsync("Google:ServiceAccountJson", "{\"secret\":true}");
        await host.Settings.SetAsync("ClubName", "배구부");

        var all = await host.Settings.GetAllAsync();

        Assert.DoesNotContain("Google:ServiceAccountJson", all.Keys);
        Assert.Contains("ClubName", all.Keys);
    }

    private static async Task<(string Id1, string Id2)> SeedUsersAsync(TestDb db)
    {
        await using var ctx = db.CreateContext();
        var u1 = NewUser("2025120101");
        var u2 = NewUser("2025120102");
        ctx.Users.AddRange(u1, u2);
        await ctx.SaveChangesAsync();
        return (u1.Id, u2.Id);
    }

    private static ApplicationUser NewUser(string studentNumber) => new()
    {
        Id = $"user-{studentNumber}",
        UserName = studentNumber,
        NormalizedUserName = studentNumber.ToUpperInvariant(),
        StudentNumber = studentNumber,
        Name = $"부원{studentNumber[^2..]}",
        Department = "테스트학과",
        IsActive = true,
        JoinedAt = new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc),
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static async Task<Activity> SeedClosedActivityAsync(TestDb db, DateOnly date, params string[] userIds)
    {
        await using var ctx = db.CreateContext();
        var now = DateTime.UtcNow;
        var activity = new Activity
        {
            ActivityDate = date,
            Status = ActivityStatus.ActivityClosed,
            AttendanceOpenedAt = now,
            AttendanceClosedAt = now,
            ActivityClosedAt = now,
            CreatedByUserId = "admin-1",
            CreatedAt = now,
            UpdatedAt = now,
        };
        foreach (var userId in userIds)
        {
            activity.Attendances.Add(new Attendance
            {
                UserId = userId,
                AttendanceType = AttendanceType.Otp,
                CheckedAt = now,
                CreatedAt = now,
            });
        }

        ctx.Activities.Add(activity);
        await ctx.SaveChangesAsync();
        return activity;
    }
}

/// <summary>공통 학기 서식 전략(SemesterTabFormat) — 실측 서식(Design/sheet-formats.md) 기반 파싱·기록 검증.</summary>
public sealed class SemesterTabFormatTests
{
    [Fact]
    public void Parse_실측_서식에서_출석_활동만_가져온다()
    {
        // 2026-2 탭 실측 형태: 헤더 1행, 0열 라벨, "1"=출석, 집계/OT/공휴일 열 혼재
        var tab = new SheetTab("2026-2",
        [
            ["총 인원 : ", "OT 참여", "26. 09. 11. 금", "26. 09. 18. 금", "9월 출석횟수", "26. 09. 25. 금"],
            ["박유진 24 만화", "1", "1", "1", "2", "- 추석 연휴 -"],
            ["김하영 25 게임", "", "", "", "0", ""],
            ["김자형 25 만화", "", "", "1", "1", ""],
        ]);
        var format = new SemesterTabFormat("2026-2", new DateOnly(2026, 8, 1), new DateOnly(2027, 1, 31));

        var data = format.Parse(tab);

        Assert.Equal(2, data.Activities.Count); // 09.25는 전원 불참(공휴일) → 제외, 집계/OT 열 제외
        var first = data.Activities[0];
        Assert.Equal(new DateOnly(2026, 9, 11), first.Date);
        Assert.Equal(["박유진 24 만화"], first.AttendedRowLabels);
        Assert.Equal(["박유진 24 만화", "김자형 25 만화"], data.Activities[1].AttendedRowLabels);
    }

    [Fact]
    public void Parse_비0채움_날짜와_불참_표기도_처리한다()
    {
        // 2024-1 탭 실측 형태: "24. 3. 8. 금", "0"(불참)
        var tab = new SheetTab("2024-1",
        [
            ["총 인원 : 42", "24. 3. 8. 금", "3월출석횟수(개인)"],
            ["소소명 23 패션", "1", "1"],
            ["구은회 24 패션", "0", "1"],
        ]);
        var format = new SemesterTabFormat("2024-1", new DateOnly(2024, 2, 1), new DateOnly(2024, 7, 31));

        var data = format.Parse(tab);

        var activity = Assert.Single(data.Activities);
        Assert.Equal(new DateOnly(2024, 3, 8), activity.Date);
        Assert.Equal(["소소명 23 패션"], activity.AttendedRowLabels); // "0"은 불참
    }

    [Fact]
    public void BuildColumn_날짜_열이_있으면_그_열에_덮어쓴다()
    {
        var tab = new SheetTab("2026-2",
        [
            ["총 인원 : ", "26. 10. 02. 금", "26. 10. 09. 금", "10월 출석횟수"],
            ["박유진 24 만화", "1", "", "1"],
            ["김하영 25 게임", "", "", "0"],
            ["김자형 25 만화", "", "", "0"],
        ]);
        var format = new SemesterTabFormat("2026-2", new DateOnly(2026, 8, 1), new DateOnly(2027, 1, 31));

        var target = format.BuildColumn(tab, new DateOnly(2026, 10, 9), new HashSet<string> { "김하영 25 게임" });

        Assert.False(target.InsertColumn);
        Assert.Equal(3, target.ColumnIndex); // "26. 10. 09. 금" 열(1-based: 1=총인원, 2=10.02, 3=10.09)
        Assert.Equal(2, target.Column.FirstRowIndex); // 헤더 제외, 부원 행부터
        Assert.Equal(["", "1", ""], target.Column.Cells); // 행-정렬된 출석 표시
    }

    [Fact]
    public void BuildColumn_날짜_사이에_열을_삽입한다()
    {
        var tab = new SheetTab("2026-2",
        [
            ["총 인원 : ", "26. 10. 02. 금", "26. 10. 16. 금", "10월 출석횟수"],
            ["박유진 24 만화", "1", "", "1"],
            ["김하영 25 게임", "", "", "0"],
        ]);
        var format = new SemesterTabFormat("2026-2", new DateOnly(2026, 8, 1), new DateOnly(2027, 1, 31));

        var target = format.BuildColumn(tab, new DateOnly(2026, 10, 9), new HashSet<string> { "박유진 24 만화" });

        Assert.True(target.InsertColumn);
        Assert.Equal(3, target.ColumnIndex); // 10.02(2)와 10.16(3) 사이 — 10.16 앞에 삽입
        Assert.Equal(1, target.Column.FirstRowIndex); // 헤더 포함
        Assert.Equal("26. 10. 09. 금", target.Column.Cells[0]); // 그 탭의 0채움 서식+금요일
        Assert.Equal(["26. 10. 09. 금", "1", ""], target.Column.Cells);
    }

    [Fact]
    public void BuildColumn_이후_날짜가_없으면_마지막_날짜_뒤에_삽입하고_서식을_따른다()
    {
        var tab = new SheetTab("2024-1",
        [
            ["총 인원 : ", "24. 3. 8. 금", "3월출석횟수(개인)"],
            ["소소명 23 패션", "1", "1"],
        ]);
        var format = new SemesterTabFormat("2024-1", new DateOnly(2024, 2, 1), new DateOnly(2024, 7, 31));

        var target = format.BuildColumn(tab, new DateOnly(2024, 3, 12), new HashSet<string>());

        Assert.True(target.InsertColumn);
        Assert.Equal(3, target.ColumnIndex); // 마지막 날짜 열(2) 뒤(집계 열 앞)
        Assert.Equal("24. 3. 12. 화", target.Column.Cells[0]); // 비0채움 서식 + 화요일
    }

    [Theory]
    [InlineData("2023-2", "2023-09-01", true)]
    [InlineData("2023-2", "2024-02-01", false)]
    [InlineData("2024-1", "2024-03-08", true)]
    [InlineData("2024-2", "2024-09-06", true)]
    [InlineData("2025-1", "2025-03-14", true)]
    [InlineData("2025-2", "2025-08-29", true)]
    [InlineData("2026-1", "2026-03-06", true)]
    [InlineData("2026-2", "2026-09-11", true)]
    [InlineData("2026-2", "2027-02-01", false)]
    public void 전_학기_전략이_날짜_범위를_커버한다(string tabName, string date, bool expected)
    {
        var format = SemesterTabFormat.All().Single(f => f.TabName == tabName);

        Assert.Equal(expected, format.CoversDate(DateOnly.Parse(date)));
    }

    [Fact]
    public void 리졸버가_전_학기_탭과_비출석_탭을_구분한다()
    {
        var resolver = new SheetFormatResolver(SemesterTabFormat.All());

        Assert.NotNull(resolver.ResolveByTabName("2026-2"));
        Assert.NotNull(resolver.ResolveByDate(new DateOnly(2026, 10, 12)));
        Assert.Null(resolver.ResolveByTabName("회비"));
    }
}
