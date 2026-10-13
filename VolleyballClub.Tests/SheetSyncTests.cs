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

    public List<(string TabName, int ColumnIndex)> Deletions { get; } = [];

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

    public Task DeleteColumnAsync(
        string spreadsheetId, string tabName, int columnIndex, CancellationToken ct = default)
    {
        Deletions.Add((tabName, columnIndex));
        return Task.CompletedTask;
    }
}

/// <summary>학기 서식 스텁 — 파싱 결과와 열 생성을 테스트에서 주입한다.</summary>
public sealed class FakeSemesterFormat(string tabName = "2026-1") : ISemesterSheetFormat
{
    public DateOnly From { get; init; } = new(2026, 3, 1);

    public DateOnly To { get; init; } = new(2026, 6, 30);

    public SheetSemesterData Data { get; set; } = new([]);

    public Func<DateOnly, IReadOnlySet<string>, SheetWriteTarget>? BuildHandler { get; set; }

    public Func<DateOnly, IReadOnlySet<string>, SheetWriteTarget?>? BuildLiveHandler { get; set; }

    public string TabName => tabName;

    public bool CoversDate(DateOnly date) => date >= From && date <= To;

    public SheetSemesterData Parse(SheetTab tab) => Data;

    public SheetWriteTarget BuildColumn(SheetTab tab, DateOnly activityDate, IReadOnlySet<string> attendedRowLabels) =>
        BuildHandler?.Invoke(activityDate, attendedRowLabels)
        ?? new SheetWriteTarget(false, 2, new SheetColumn(2, [activityDate.ToString("M/d")]));

    public SheetWriteTarget? BuildLiveColumn(SheetTab tab, DateOnly activityDate, IReadOnlySet<string> attendedRowLabels) =>
        BuildLiveHandler?.Invoke(activityDate, attendedRowLabels);

    public Func<DateOnly, int?>? FindColumnHandler { get; set; }

    public int? FindDateColumnIndex(SheetTab tab, DateOnly date) =>
        FindColumnHandler?.Invoke(date);
}

/// <summary>SheetSyncService 테스트 호스트 — 시트 클라이언트·전략·시트 정의를 직접 조립한다.</summary>
public sealed class SheetSyncHost : IAsyncDisposable
{
    public TestDb Db { get; } = new();

    public FakeSheetClient Client { get; } = new();

    public ISemesterSheetFormat Format { get; }

    public SheetSyncService Sync { get; }

    public int SheetId { get; private set; }

    public SheetSyncHost(ISemesterSheetFormat? format = null)
    {
        Format = format ?? new FakeSemesterFormat();
        Sync = new SheetSyncService(
            Db.Factory,
            Client,
            new SheetFormatResolver([Format]),
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
    public async Task 매핑_저장_즉시_과거_출석이_백필된다()
    {
        await using var host = new SheetSyncHost(new FakeSemesterFormat
        {
            Data = new SheetSemesterData([new SheetActivityColumn(new DateOnly(2026, 3, 10), ["박유진 24 만콘"])]),
        });
        var (user1, _) = await SeedUsersAsync(host.Db);
        await host.SeedSheetAsync();
        host.Client.Tabs.Add(new SheetTab("2026-1", []));
        await host.Sync.SyncAsync(); // 첫 가져오기 — 매핑 없음 → 출석 0

        // UI에서 매핑 저장 → 즉시 동기화 → 백필되어야 한다(재기동 불필요)
        var mapping = new SheetMemberMappingService(
            host.Db.Factory,
            host.Client,
            new SheetFormatResolver([host.Format]),
            new FakeCurrentUser("admin-1", isAdmin: true),
            new AuditLogService(host.Db.Factory, new FakeCurrentUser("admin-1", isAdmin: true),
                new FixedTimeProvider(new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc))),
            host.Sync,
            new FixedTimeProvider(new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc)));
        var report = await mapping.SetMappingAsync(host.SheetId, "2026-1", user1, "박유진 24 만콘");

        Assert.NotNull(report);
        Assert.Equal(1, report!.ImportedAttendances);
        await using var db = host.Db.CreateContext();
        Assert.Equal(1, await db.Attendances.CountAsync(a => a.AttendanceType == AttendanceType.Sheet));
    }

    [Fact]
    public async Task 시트가져오기_활동은_매핑된_학기만_미출석으로_표시한다()
    {
        // 전략은 2026-1(2/1~7/31)만 커버 — 매핑도 2026-1 탭에만 존재
        var format = new FakeSemesterFormat("2026-1") { From = new DateOnly(2026, 2, 1), To = new DateOnly(2026, 7, 31) };
        await using var host = new ServiceHost(formatResolver: new SheetFormatResolver([format]));
        var user = await host.SeedUserAsync("박유진", "2025120101");
        await SeedImportedActivityAsync(host.Db, new DateOnly(2026, 3, 10)); // 매핑 학기
        await SeedImportedActivityAsync(host.Db, new DateOnly(2026, 9, 11)); // 비회원 학기(2026-2)

        await using (var db = host.Db.CreateContext())
        {
            var sheet = new SheetDefinition
            {
                Name = "출석부",
                SpreadsheetId = "ss-test",
                Kind = SheetKind.Attendance,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            db.SheetDefinitions.Add(sheet);
            await db.SaveChangesAsync();
            db.SheetMemberMappings.Add(new SheetMemberMapping
            {
                SheetDefinitionId = sheet.Id,
                TabName = "2026-1",
                RowLabel = "박유진 24 만콘",
                UserId = user,
            });
            await db.SaveChangesAsync();
        }

        // 기록: 매핑 학기는 미출석으로 표시, 비회원 학기는 아예 표시하지 않는다
        var march = await host.Attendance.GetMyRecordsAsync(user, 2026, 3);
        Assert.Single(march.Records);
        Assert.False(march.Records[0].IsAttended);
        Assert.Equal(1, march.MonthActivities);

        var september = await host.Attendance.GetMyRecordsAsync(user, 2026, 9);
        Assert.Empty(september.Records);
        Assert.Equal(0, september.MonthActivities);

        // 달력: 비회원 학기 활동은 결석일로 표시하지 않는다
        var calendar = await host.Attendance.GetMyCalendarAsync(user, 2026, 9);
        Assert.DoesNotContain(calendar.Days, d => d.Kind == CalendarDayKind.Missed);

        // 학기 통계(고정 시각 2026-10-01 → 2026-2 범위): 비회원 학기 활동은 분모 제외
        var stats = await host.Attendance.GetMySemesterStatsAsync(user);
        Assert.Equal(0, stats.SemesterActivities);
    }

    [Fact]
    public async Task SyncAsync_신형_탭에서_예정과_없음을_마크로_가져온다()
    {
        // 신형(2026-2) 실측 서식 — 1행 상태 Enum, 2행 날짜, 3행~ 부원(출석/미출석/활동 불가)
        await using var host = new SheetSyncHost(
            new SemesterTabFormat("2026-2", new DateOnly(2026, 8, 1), new DateOnly(2027, 1, 31)));
        var (user1, _) = await SeedUsersAsync(host.Db);
        await host.SeedSheetAsync();
        await host.SeedMappingAsync("2026-2", "박유진 24 만화", user1);
        host.Client.Tabs.Add(SemesterTabFormatTests.NewFormatTab());

        var report = await host.Sync.SyncAsync();

        await using var db = host.Db.CreateContext();
        var activities = await db.Activities.OrderBy(a => a.ActivityDate).ToListAsync();
        Assert.Equal(2, activities.Count); // 완료 열(9/11, 9/18)만 활동으로
        Assert.All(activities, a => Assert.Equal(ActivityStatus.ActivityClosed, a.Status));
        Assert.Equal(2, await db.Attendances.CountAsync(a => a.AttendanceType == AttendanceType.Sheet));
        Assert.Equal(2, report.ImportedActivities);

        var marks = await db.SheetDayMarks.ToDictionaryAsync(m => m.Date, m => m.Kind);
        Assert.Equal(4, marks.Count); // 없음 3 + 예정 1
        Assert.Equal(SheetDayKind.NoActivity, marks[new DateOnly(2026, 9, 25)]); // 사유 괄호는 버림
        Assert.Equal(SheetDayKind.NoActivity, marks[new DateOnly(2026, 10, 2)]);
        Assert.Equal(SheetDayKind.NoActivity, marks[new DateOnly(2026, 10, 9)]);
        Assert.Equal(SheetDayKind.Scheduled, marks[new DateOnly(2026, 10, 16)]);
    }

    [Fact]
    public async Task SyncAsync_시트에서_완료로_바뀐_날의_마크를_지운다()
    {
        await using var host = new SheetSyncHost(
            new SemesterTabFormat("2026-2", new DateOnly(2026, 8, 1), new DateOnly(2027, 1, 31)));
        var (user1, _) = await SeedUsersAsync(host.Db);
        await host.SeedSheetAsync();
        await host.SeedMappingAsync("2026-2", "박유진 24 만화", user1);
        host.Client.Tabs.Add(SemesterTabFormatTests.NewFormatTab());
        await host.Sync.SyncAsync(); // 10/16 예정 마크 생성

        // 시트에서 10/16이 완료로 전환되고 박유진 출석 표기가 생긴다
        var rows = SemesterTabFormatTests.NewFormatTab().Rows.Select(r => r.ToList()).ToList();
        rows[0][8] = "활동 완료";
        rows[2][8] = "출석";
        host.Client.Tabs[0] = new SheetTab("2026-2", rows);

        await host.Sync.SyncAsync();

        await using var db = host.Db.CreateContext();
        var marks = await db.SheetDayMarks.ToDictionaryAsync(m => m.Date, m => m.Kind);
        Assert.Equal(3, marks.Count); // 10/16 마크 제거
        Assert.False(marks.ContainsKey(new DateOnly(2026, 10, 16)));
        Assert.Equal(3, await db.Activities.CountAsync()); // 10/16 활동으로 가져옴
        Assert.Equal(3, await db.Attendances.CountAsync(a => a.AttendanceType == AttendanceType.Sheet));
    }

    [Fact]
    public async Task ExportClosedActivityAsync_신형_기존열에_상태행과_마크를_쓴다()
    {
        await using var host = new SheetSyncHost(
            new SemesterTabFormat("2026-2", new DateOnly(2026, 8, 1), new DateOnly(2027, 1, 31)));
        var (user1, _) = await SeedUsersAsync(host.Db);
        await host.SeedSheetAsync();
        await host.SeedMappingAsync("2026-2", "박유진 24 만화", user1);
        var activity = await SeedClosedActivityAsync(host.Db, new DateOnly(2026, 10, 16), user1);
        host.Client.Tabs.Add(SemesterTabFormatTests.NewFormatTab());
        await using (var db = host.Db.CreateContext())
        {
            db.SheetDayMarks.Add(new SheetDayMark { Date = new DateOnly(2026, 10, 16), Kind = SheetDayKind.Scheduled });
            await db.SaveChangesAsync();
        }

        Assert.True(await host.Sync.ExportClosedActivityAsync(activity.Id));

        // 부원 마크(3행~)와 상태행(1행) 갈라 쓰기 — 2행 헤더는 건너뛴다
        Assert.Equal(2, host.Client.Writes.Count);
        var marks = host.Client.Writes[0];
        Assert.False(marks.Insert);
        Assert.Equal(9, marks.ColumnIndex); // 26. 10. 16 열
        Assert.Equal(3, marks.Column.FirstRowIndex);
        Assert.Equal(["출석", "미출석", "미출석"], marks.Column.Cells);
        var status = host.Client.Writes[1];
        Assert.Equal(9, status.ColumnIndex);
        Assert.Equal(1, status.Column.FirstRowIndex);
        Assert.Equal(["활동 완료"], status.Column.Cells);

        await using var db2 = host.Db.CreateContext();
        Assert.False(await db2.SheetDayMarks.AnyAsync(m => m.Date == new DateOnly(2026, 10, 16))); // 마크 즉시 제거
        Assert.True(await db2.Activities.Where(a => a.Id == activity.Id).Select(a => a.SheetExportedAt != null).FirstAsync());
    }

    [Fact]
    public async Task ExportScheduledActivityAsync_예정열에_상태만_기록한다()
    {
        await using var host = new SheetSyncHost(
            new SemesterTabFormat("2026-2", new DateOnly(2026, 8, 1), new DateOnly(2027, 1, 31)));
        await host.SeedSheetAsync();
        var activity = await SeedClosedActivityAsync(host.Db, new DateOnly(2026, 10, 16));
        host.Client.Tabs.Add(SemesterTabFormatTests.NewFormatTab());

        Assert.True(await host.Sync.ExportScheduledActivityAsync(activity.Id));

        Assert.Contains(host.Client.Writes, w =>
            w.ColumnIndex == 9 && w.Column.FirstRowIndex == 1 && w.Column.Cells.SequenceEqual(["활동 예정"]));
    }

    [Fact]
    public async Task SyncAsync_시트_수정이_기존_출석에_반영된다()
    {
        // 사용자 핵심 요구: 과거 활동의 시트를 수기로 수정하면 서버가 따라간다(시트가 원천 — 병합)
        var format = new FakeSemesterFormat
        {
            Data = new SheetSemesterData([new SheetActivityColumn(new DateOnly(2026, 3, 10), ["박유진 24 만화"])]),
        };
        await using var host = new SheetSyncHost(format);
        var (user1, user2) = await SeedUsersAsync(host.Db);
        await host.SeedSheetAsync();
        await host.SeedMappingAsync("2026-1", "박유진 24 만화", user1);
        await host.SeedMappingAsync("2026-1", "김하영 25 게임", user2);
        host.Client.Tabs.Add(new SheetTab("2026-1", []));
        await host.Sync.SyncAsync(); // 박유진 출석으로 생성

        // 시트에서 박유진→김하영으로 수기 수정했다
        format.Data = new SheetSemesterData([new SheetActivityColumn(new DateOnly(2026, 3, 10), ["김하영 25 게임"])]);
        var second = await host.Sync.SyncAsync();

        Assert.Equal(1, second.ImportedActivities); // 갱신으로 집계
        Assert.Equal(1, second.ImportedAttendances);
        await using var db = host.Db.CreateContext();
        var activity = await db.Activities.Include(a => a.Attendances).SingleAsync();
        var userIds = activity.Attendances.Select(a => a.UserId).ToList();
        Assert.Equal([user2], userIds); // 박유진 제거, 김하영 추가 — 시트를 따라감
    }

    [Fact]
    public async Task SyncAsync_진행중_활동_날짜가_완료로_바뀌면_병합하지_않는다()
    {
        // 활동 중 출석 보호 — 마감 전에는 시트 완료 표기를 병합하지 않고 경고만 남긴다
        await using var host = new SheetSyncHost(new FakeSemesterFormat
        {
            Data = new SheetSemesterData([new SheetActivityColumn(new DateOnly(2026, 3, 10), ["박유진"])]),
        });
        var (user1, user2) = await SeedUsersAsync(host.Db);
        await host.SeedSheetAsync();
        await host.SeedMappingAsync("2026-1", "박유진", user1);
        await host.SeedMappingAsync("2026-1", "김하영", user2);
        host.Client.Tabs.Add(new SheetTab("2026-1", []));
        await using (var ctx = host.Db.CreateContext())
        {
            var now = DateTime.UtcNow;
            var open = new Activity
            {
                ActivityDate = new DateOnly(2026, 3, 10),
                Status = ActivityStatus.AttendanceOpen,
                AttendanceOpenedAt = now,
                CreatedByUserId = "admin-1",
                CreatedAt = now,
                UpdatedAt = now,
            };
            open.Attendances.Add(new Attendance { UserId = user2, AttendanceType = AttendanceType.Otp, CheckedAt = now, CreatedAt = now });
            ctx.Activities.Add(open);
            await ctx.SaveChangesAsync();
        }

        var report = await host.Sync.SyncAsync();

        Assert.Contains(report.Warnings, w => w.Contains("진행 중 활동과"));
        await using var db = host.Db.CreateContext();
        var attendance = await db.Attendances.SingleAsync();
        Assert.Equal(user2, attendance.UserId); // OTP 출석 보존 — 병합 안 함
    }

    [Fact]
    public async Task SyncAsync_미매핑_부원의_출석은_병합에서_보존된다()
    {
        // 시트가 표현할 수 없는(이 탭에 행이 없는) 부원의 출석은 시트 원칙 병합이 지우지 않는다
        await using var host = new SheetSyncHost(new FakeSemesterFormat
        {
            Data = new SheetSemesterData([new SheetActivityColumn(new DateOnly(2026, 3, 10), ["박유진"])]),
        });
        var (user1, user2) = await SeedUsersAsync(host.Db); // user2는 시트에 매핑되지 않음
        await host.SeedSheetAsync();
        await host.SeedMappingAsync("2026-1", "박유진", user1);
        host.Client.Tabs.Add(new SheetTab("2026-1", []));
        await using (var ctx = host.Db.CreateContext())
        {
            var now = DateTime.UtcNow;
            var activity = new Activity
            {
                ActivityDate = new DateOnly(2026, 3, 10),
                Status = ActivityStatus.ActivityClosed,
                AttendanceOpenedAt = now,
                AttendanceClosedAt = now,
                ActivityClosedAt = now,
                CreatedByUserId = "admin-1",
                CreatedAt = now,
                UpdatedAt = now,
                SheetExportedAt = now,
            };
            activity.Attendances.Add(new Attendance { UserId = user2, AttendanceType = AttendanceType.Otp, CheckedAt = now, CreatedAt = now });
            ctx.Activities.Add(activity);
            await ctx.SaveChangesAsync();
        }

        await host.Sync.SyncAsync(); // 시트는 박유진만 표시 — 미매핑 user2의 출석이 지워지면 안 된다

        await using var db = host.Db.CreateContext();
        var attendances = await db.Attendances.Select(a => a.UserId).ToListAsync();
        Assert.Contains(user1, attendances); // 시트 표시 병합
        Assert.Contains(user2, attendances); // 미매핑 출석 보존
    }

    [Fact]
    public async Task SyncAsync_완료를_없음으로_되돌리면_시트유래_활동을_지운다()
    {
        // 시트가 원천: 완료로 가져왔던 날을 없음으로 되돌리면 시트 유래 활동이 지워지고 캘린더에 없음 마크가 남는다
        var format = new FakeSemesterFormat
        {
            Data = new SheetSemesterData([new SheetActivityColumn(new DateOnly(2026, 3, 10), ["박유진"])]),
        };
        await using var host = new SheetSyncHost(format);
        var (user1, _) = await SeedUsersAsync(host.Db);
        await host.SeedSheetAsync();
        await host.SeedMappingAsync("2026-1", "박유진", user1);
        host.Client.Tabs.Add(new SheetTab("2026-1", []));
        await host.Sync.SyncAsync(); // 완료 열 → 활동+출석 생성
        await using (var db = host.Db.CreateContext())
        {
            Assert.Equal(1, await db.Activities.CountAsync());
        }

        format.Data = new SheetSemesterData(
            [new SheetActivityColumn(new DateOnly(2026, 3, 10), [], SheetDayKind.NoActivity)]);
        var second = await host.Sync.SyncAsync();

        Assert.Contains(second.Warnings, w => w.Contains("시트 유래 활동을 제거"));
        await using var db2 = host.Db.CreateContext();
        Assert.Equal(0, await db2.Activities.CountAsync()); // 시트 유래 활동 제거
        Assert.Equal(0, await db2.Attendances.CountAsync());
        var mark = await db2.SheetDayMarks.SingleAsync();
        Assert.Equal(new DateOnly(2026, 3, 10), mark.Date);
        Assert.Equal(SheetDayKind.NoActivity, mark.Kind); // 캘린더에는 활동 없음으로 표시
    }

    [Fact]
    public async Task SyncAsync_앱_활동이_있는_날의_없음_표기는_활동을_지우지_않는다()
    {
        await using var host = new SheetSyncHost(new FakeSemesterFormat
        {
            Data = new SheetSemesterData(
                [new SheetActivityColumn(new DateOnly(2026, 3, 10), [], SheetDayKind.NoActivity)]),
        });
        var (user1, _) = await SeedUsersAsync(host.Db);
        await host.SeedSheetAsync();
        await host.SeedMappingAsync("2026-1", "박유진", user1);
        host.Client.Tabs.Add(new SheetTab("2026-1", []));
        await SeedClosedActivityAsync(host.Db, new DateOnly(2026, 3, 10), user1); // 앱 생성(OTP 실적)

        var report = await host.Sync.SyncAsync();

        Assert.Contains(report.Warnings, w => w.Contains("앱 활동이 있어"));
        await using var db = host.Db.CreateContext();
        Assert.Equal(1, await db.Activities.CountAsync()); // 보존
        Assert.Equal(1, await db.Attendances.CountAsync());
    }

    [Fact]
    public async Task ResyncAsync_진행중_활동을_임시기록하고_전체를_재반영한다()
    {
        await using var host = new SheetSyncHost(
            new SemesterTabFormat("2026-2", new DateOnly(2026, 8, 1), new DateOnly(2027, 1, 31)));
        var (user1, _) = await SeedUsersAsync(host.Db);
        await host.SeedSheetAsync();
        await host.SeedMappingAsync("2026-2", "박유진 24 만화", user1);
        host.Client.Tabs.Add(SemesterTabFormatTests.NewFormatTab());
        await using (var ctx = host.Db.CreateContext())
        {
            var now = DateTime.UtcNow;
            var open = new Activity
            {
                ActivityDate = new DateOnly(2026, 10, 16),
                Status = ActivityStatus.AttendanceOpen,
                AttendanceOpenedAt = now,
                CreatedByUserId = "admin-1",
                CreatedAt = now,
                UpdatedAt = now,
            };
            open.Attendances.Add(new Attendance { UserId = user1, AttendanceType = AttendanceType.Otp, CheckedAt = now, CreatedAt = now });
            ctx.Activities.Add(open);
            await ctx.SaveChangesAsync();
        }

        var report = await host.Sync.ResyncAsync();

        // 1) 진행 중 활동(10/16 예정 열) 임시 기록 — 상태행 '활동 예정' 유지, 현재 출석자만 마크
        Assert.Contains(host.Client.Writes, w =>
            w.ColumnIndex == 9 && w.Column.FirstRowIndex == 3 && w.Column.Cells.SequenceEqual(["출석", "", ""]));
        Assert.Contains(host.Client.Writes, w =>
            w.ColumnIndex == 9 && w.Column.FirstRowIndex == 1 && w.Column.Cells.SequenceEqual(["활동 예정"]));

        // 2) 전체 재반영 — 완료 열 2건(9/11, 9/18) 신규 생성, 진행 중 활동·예정 마크는 그대로
        Assert.Equal(2, report.ImportedActivities);
        await using var db = host.Db.CreateContext();
        Assert.Equal(3, await db.Activities.CountAsync()); // 진행 1 + 완료 2
        Assert.Equal(4, await db.SheetDayMarks.CountAsync()); // 없음 3 + 예정 1
        var openAttendance = await db.Attendances.SingleAsync(a => a.AttendanceType == AttendanceType.Otp);
        Assert.Equal(user1, openAttendance.UserId); // 진행 중 출석은 건드리지 않는다
    }

    [Fact]
    public async Task RemoveCancelledActivityAsync_그_날짜_열을_통째로_삭제한다()
    {
        await using var host = new SheetSyncHost(
            new SemesterTabFormat("2026-2", new DateOnly(2026, 8, 1), new DateOnly(2027, 1, 31)));
        await host.SeedSheetAsync();
        var activity = await SeedClosedActivityAsync(host.Db, new DateOnly(2026, 9, 18));
        host.Client.Tabs.Add(SemesterTabFormatTests.NewFormatTab());

        Assert.True(await host.Sync.RemoveCancelledActivityAsync(activity.Id));

        // 9/18 열(1-based 4) 삭제 — 내용 지우기가 아니라 열 제거
        Assert.Equal([("2026-2", 4)], host.Client.Deletions);
        Assert.Empty(host.Client.Writes);

        // 열이 없는 날짜 — 삭제 없이 true
        var other = await SeedClosedActivityAsync(host.Db, new DateOnly(2026, 10, 10));
        host.Client.Deletions.Clear();
        Assert.True(await host.Sync.RemoveCancelledActivityAsync(other.Id));
        Assert.Empty(host.Client.Deletions);
    }

    private static async Task<Activity> SeedImportedActivityAsync(TestDb db, DateOnly date)
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
            CreatedByUserId = Activity.SheetImportCreator,
            CreatedAt = now,
            UpdatedAt = now,
            SheetExportedAt = now,
        };
        ctx.Activities.Add(activity);
        await ctx.SaveChangesAsync();
        return activity;
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
    /// <summary>신형(2026-2) 실측 서식 픽스처 — 1행 활동 상태 Enum, 2행 날짜 헤더, 3행~ 부원.
    /// 완료 2(9/11, 9/18) + 없음 3(9/25 추석, 10/2, 10/9 한글날) + 예정 1(10/16).</summary>
    internal static SheetTab NewFormatTab() => new("2026-2",
    [
        ["활동 상태", "활동 완료", "활동 완료", "활동 완료", "활동 없음(추석 연휴)", "", "활동 없음", "활동 없음(한글날)", "활동 예정"],
        ["총 인원 : ", "OT 참여", "26. 09. 11. 금", "26. 09. 18. 금", "26. 09. 25. 금", "9월 출석횟수", "26. 10. 02. 금", "26. 10. 09. 금", "26. 10. 16. 금"],
        ["박유진 24 만화", "출석", "출석", "출석", "", "2", "", "", ""],
        ["김하영 25 게임", "활동 불가", "활동 불가", "활동 불가", "", "0", "", "", ""],
        ["김자형 25 만화", "미출석", "출석", "미출석", "", "0", "", "", ""],
        // 부원 구간 끝 — 아래는 집계·명단이라 부원으로 읽지 않는다
        ["총 참여자 수", "15", "35", "30", "0", "", "0", "0", "0"],
        ["탈퇴/퇴출자", "", "", "", "", "", "", "", ""],
        ["유지원 25 공연 (퇴)", "출석", "1", "", "", "", "", "", "출석"], // 마크가 있어도 무시
    ]);

    [Fact]
    public void Parse_신형_서식에서_상태와_출석을_가져온다()
    {
        var format = new SemesterTabFormat("2026-2", new DateOnly(2026, 8, 1), new DateOnly(2027, 1, 31));

        var data = format.Parse(NewFormatTab());

        Assert.Equal(6, data.Activities.Count); // 완료 2 + 없음 3 + 예정 1(집계·OT 열 제외)
        var byDate = data.Activities.ToDictionary(a => a.Date);
        Assert.Equal(SheetDayKind.Completed, byDate[new DateOnly(2026, 9, 11)].Kind);
        Assert.Equal(["박유진 24 만화", "김자형 25 만화"], byDate[new DateOnly(2026, 9, 11)].AttendedRowLabels);
        Assert.Equal(["박유진 24 만화"], byDate[new DateOnly(2026, 9, 18)].AttendedRowLabels); // 활동 불가·미출석은 흡수되어 기록 없음
        Assert.Equal(SheetDayKind.NoActivity, byDate[new DateOnly(2026, 9, 25)].Kind); // 사유 괄호는 버림
        Assert.Equal(SheetDayKind.NoActivity, byDate[new DateOnly(2026, 10, 2)].Kind);
        Assert.Equal(SheetDayKind.NoActivity, byDate[new DateOnly(2026, 10, 9)].Kind);
        Assert.Equal(SheetDayKind.Scheduled, byDate[new DateOnly(2026, 10, 16)].Kind);
        Assert.Empty(byDate[new DateOnly(2026, 10, 16)].AttendedRowLabels);
    }

    [Fact]
    public void BuildColumn_신형_기존열에_상태행과_출석마크를_분리해_쓴다()
    {
        var format = new SemesterTabFormat("2026-2", new DateOnly(2026, 8, 1), new DateOnly(2027, 1, 31));

        var target = format.BuildColumn(NewFormatTab(), new DateOnly(2026, 9, 18), new HashSet<string> { "김자형 25 만화" });

        Assert.False(target.InsertColumn);
        Assert.Equal(4, target.ColumnIndex); // "26. 09. 18. 금" 열(1=라벨, 2=OT, 3=9/11, 4=9/18)
        Assert.Equal(3, target.Column.FirstRowIndex); // 신형은 부원이 3행부터(상태 1·헤더 2)
        Assert.Equal(["미출석", "미출석", "출석"], target.Column.Cells);
        Assert.NotNull(target.StatusColumn);
        Assert.Equal(1, target.StatusColumn!.FirstRowIndex);
        Assert.Equal(["활동 완료"], target.StatusColumn.Cells);
    }

    [Fact]
    public void BuildColumn_신형_날짜_사이에_상태와_헤더를_함께_삽입한다()
    {
        var format = new SemesterTabFormat("2026-2", new DateOnly(2026, 8, 1), new DateOnly(2027, 1, 31));

        var target = format.BuildColumn(NewFormatTab(), new DateOnly(2026, 10, 10), new HashSet<string>());

        Assert.True(target.InsertColumn);
        Assert.Equal(9, target.ColumnIndex); // 10/09(8)과 10/16(9) 사이 — 10/16 앞에 삽입
        Assert.Equal(["활동 완료", "26. 10. 10. 토", "미출석", "미출석", "미출석"], target.Column.Cells);
        // 총 참여자 수 행(6행) 수식이 별도 쓰기로 나간다 — 부원 3~5행 카운트
        Assert.NotNull(target.StatusColumn);
        Assert.Equal(6, target.StatusColumn!.FirstRowIndex);
        Assert.Equal(["=COUNTIF(I3:I5,\"출석\")"], target.StatusColumn.Cells);
    }

    [Fact]
    public void BuildLiveColumn_신형은_예정상태와_출석마크만_기록한다()
    {
        var format = new SemesterTabFormat("2026-2", new DateOnly(2026, 8, 1), new DateOnly(2027, 1, 31));

        // 이미 예정인 열 — 현재 출석자만 표시(미출석자는 공란), 상태행은 예정 유지
        var existing = format.BuildLiveColumn(NewFormatTab(), new DateOnly(2026, 10, 16), new HashSet<string> { "김자형 25 만화" });
        Assert.NotNull(existing);
        Assert.False(existing!.InsertColumn);
        Assert.Equal(9, existing.ColumnIndex);
        Assert.Equal(["", "", "출석"], existing.Column.Cells); // 미출석자는 공란 — 예정 열 공란 규칙
        Assert.Equal(["활동 예정"], existing.StatusColumn!.Cells);

        // 완료·없음으로 확정된 열은 되돌리지 않는다
        Assert.Null(format.BuildLiveColumn(NewFormatTab(), new DateOnly(2026, 9, 11), new HashSet<string>()));
        Assert.Null(format.BuildLiveColumn(NewFormatTab(), new DateOnly(2026, 9, 25), new HashSet<string>()));

        // 없는 날 — 상태+헤더(+출석 마크)로 새 열 삽입; 시작 직후(출석 0)면 헤더까지만
        var insert = format.BuildLiveColumn(NewFormatTab(), new DateOnly(2026, 10, 10), new HashSet<string>());
        Assert.NotNull(insert);
        Assert.True(insert!.InsertColumn);
        Assert.Equal(["활동 예정", "26. 10. 10. 토", "", "", ""], insert.Column.Cells);
    }

    [Fact]
    public void BuildLiveColumn_구형은_기록하지_않는다()
    {
        // 구형은 상태 개념이 없다 — 진행 중 임시 기록을 하지 않고 마감 때만 기록한다
        var tab = new SheetTab("2026-1",
        [
            ["총 인원 : ", "26. 03. 06. 금", "26. 03. 13. 금"],
            ["박유진 24 만콘", "1", ""],
        ]);
        var format = new SemesterTabFormat("2026-1", new DateOnly(2026, 2, 1), new DateOnly(2026, 7, 31));

        Assert.Null(format.BuildLiveColumn(tab, new DateOnly(2026, 3, 6), new HashSet<string> { "박유진 24 만콘" }));
    }

    [Fact]
    public void Parse_신형_상태_빈칸_열은_출석이_없으면_가져오지_않는다()
    {
        // 미리 만들어진 빈 날짜 열 보호 — 상태도 마크도 없는 열은 완료 활동로 오인하지 않는다
        var tab = new SheetTab("2026-2",
        [
            ["활동 상태", "활동 완료", "", "활동 예정"],
            ["총 인원 : ", "26. 10. 02. 금", "26. 10. 09. 금", "26. 10. 16. 금"],
            ["박유진 24 만화", "", "", ""],
        ]);
        var format = new SemesterTabFormat("2026-2", new DateOnly(2026, 8, 1), new DateOnly(2027, 1, 31));

        var data = format.Parse(tab);

        Assert.Equal(2, data.Activities.Count); // 상태 빈칸+출석 0인 10/9은 제외
        Assert.DoesNotContain(data.Activities, a => a.Date == new DateOnly(2026, 10, 9));
    }

    [Fact]
    public void Parse_신형_상태_빈칸이어도_출석_마크가_있으면_완료로_가져온다()
    {
        var tab = new SheetTab("2026-2",
        [
            ["활동 상태", "", "활동 예정"],
            ["총 인원 : ", "26. 10. 02. 금", "26. 10. 16. 금"],
            ["박유진 24 만화", "출석", ""],
        ]);
        var format = new SemesterTabFormat("2026-2", new DateOnly(2026, 8, 1), new DateOnly(2027, 1, 31));

        var data = format.Parse(tab);

        var completed = Assert.Single(data.Activities, a => a.Kind == SheetDayKind.Completed);
        Assert.Equal(new DateOnly(2026, 10, 2), completed.Date);
        Assert.Equal(["박유진 24 만화"], completed.AttendedRowLabels);
    }

    [Fact]
    public void Parse_신형_완료열은_출석이_0명이어도_활동으로_가져온다()
    {
        // 신형은 상태행이 원천 — 완료로 표기된 날은 실제로 활동이 있었던 것(전원 불참 포함).
        // 실측 사례: 2026-2의 10/2 열을 사용자가 '활동 완료'로 수기 수정(마크는 공란)
        var tab = new SheetTab("2026-2",
        [
            ["활동 상태", "활동 완료", "활동 없음", "활동 예정"],
            ["총 인원 : ", "26. 10. 02. 금", "26. 10. 09. 금", "26. 10. 16. 금"],
            ["박유진 24 만화", "", "", ""],
            ["김하영 25 게임", "", "", ""],
        ]);
        var format = new SemesterTabFormat("2026-2", new DateOnly(2026, 8, 1), new DateOnly(2027, 1, 31));

        var data = format.Parse(tab);

        Assert.Equal(3, data.Activities.Count);
        var completed = data.Activities[0];
        Assert.Equal(new DateOnly(2026, 10, 2), completed.Date);
        Assert.Equal(SheetDayKind.Completed, completed.Kind);
        Assert.Empty(completed.AttendedRowLabels); // 출석 0명 — 그래도 활동
    }

    [Fact]
    public void FindDateColumnIndex_날짜_열_번호를_반환한다()
    {
        var format = new SemesterTabFormat("2026-2", new DateOnly(2026, 8, 1), new DateOnly(2027, 1, 31));

        Assert.Equal(4, format.FindDateColumnIndex(NewFormatTab(), new DateOnly(2026, 9, 18))); // OT 포함 4번째 날짜 열
        Assert.Equal(9, format.FindDateColumnIndex(NewFormatTab(), new DateOnly(2026, 10, 16)));
        Assert.Null(format.FindDateColumnIndex(NewFormatTab(), new DateOnly(2026, 10, 10))); // 없는 날

        var old = new SemesterTabFormat("2026-1", new DateOnly(2026, 2, 1), new DateOnly(2026, 7, 31));
        var tab = new SheetTab("2026-1", [["총 인원 : ", "26. 03. 06. 금"], ["박유진 24 만콘", "1"]]);
        Assert.Equal(2, old.FindDateColumnIndex(tab, new DateOnly(2026, 3, 6))); // 구형도 동일 규약
    }

    [Fact]
    public void BuildColumn_구형_삽입도_총_참여자_수_수식을_채운다()
    {
        var tab = new SheetTab("2026-1",
        [
            ["총 인원 : ", "26. 03. 06. 금", "3월출석횟수(개인)"],
            ["소소명 23 패션", "1", "1"],
            ["구은회 24 패션", "0", "1"],
            ["총 참여자 수", "1", "2"],
        ]);
        var format = new SemesterTabFormat("2026-1", new DateOnly(2026, 2, 1), new DateOnly(2026, 7, 31));

        var target = format.BuildColumn(tab, new DateOnly(2026, 3, 13), new HashSet<string> { "소소명 23 패션" });

        Assert.True(target.InsertColumn);
        Assert.NotNull(target.StatusColumn); // 4행(총 참여자 수)에 SUM 수식
        Assert.Equal(4, target.StatusColumn!.FirstRowIndex);
        Assert.Equal(["=SUM(C2:C3)"], target.StatusColumn.Cells);
        Assert.Equal(["26. 03. 13. 금", "1", ""], target.Column.Cells); // 픽스처 헤더의 0채움 서식을 따름
    }

    [Fact]
    public void Parse_총_참여자_수_아래_행은_부원으로_읽지_않는다()
    {
        // 집계 행의 숫자·탈퇴자의 마크가 출석으로 해석되지 않는다(구형 "1" 포함)
        var tab = new SheetTab("2026-1",
        [
            ["총 인원 : ", "26. 03. 06. 금"],
            ["박유진 24 만콘", "1"],
            ["총 참여자 수", "35"],
            ["탈퇴/퇴출자", ""],
            ["정문경 25 애니 (탈)", "1"], // 탈퇴자 마크 — 무시
        ]);
        var format = new SemesterTabFormat("2026-1", new DateOnly(2026, 2, 1), new DateOnly(2026, 7, 31));

        var data = format.Parse(tab);

        var activity = Assert.Single(data.Activities);
        Assert.Equal(["박유진 24 만콘"], activity.AttendedRowLabels); // 35·탈퇴자 1은 무시
    }

    [Fact]
    public void Parse_끝점_없는_12월_헤더도_날짜로_인식한다()
    {
        // 실측 2026-2: 12월 헤더는 손입이라 일 뒤 점이 없다("26. 12. 04 금")
        var tab = new SheetTab("2026-2",
        [
            ["활동 상태", "활동 예정", "활동 예정", ""],
            ["총 인원 : ", "26. 11. 27. 금", "26. 12. 04 금", "12월 출석횟수"],
            ["박유진 24 만화", "", "", ""],
        ]);
        var format = new SemesterTabFormat("2026-2", new DateOnly(2026, 8, 1), new DateOnly(2027, 1, 31));

        var data = format.Parse(tab);

        Assert.Equal(2, data.Activities.Count);
        Assert.Equal(SheetDayKind.Scheduled, data.Activities[0].Kind);
        Assert.Equal(new DateOnly(2026, 11, 27), data.Activities[0].Date);
        Assert.Equal(SheetDayKind.Scheduled, data.Activities[1].Kind);
        Assert.Equal(new DateOnly(2026, 12, 4), data.Activities[1].Date); // 집계 열은 여전히 제외
    }

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
