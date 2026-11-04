using Microsoft.EntityFrameworkCore;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Domain.Entities;
using VolleyballClub.Domain.Enums;

namespace VolleyballClub.Tests;

/// <summary>'활동 없음' 자동 전환 — 과거 예정 마크 스윕, 탭명 학기 자동 생성, 활동 수정 부원 목록의 학기 한정.</summary>
public sealed class ActivityAutoExpireTests
{
    [Fact]
    public async Task 과거_예정_마크는_조회_시점에_없음으로_전환되고_미래_예정과_과거_없음은_그대로다()
    {
        await using var host = new ServiceHost(); // 고정 시각 2026-10-01 KST
        host.AsAdmin();
        await host.SeedUserAsync("김민수", "2025");
        await using (var db = host.Db.CreateContext())
        {
            db.DayMarks.AddRange(
                new DayMark { Date = new DateOnly(2026, 9, 10), Kind = DayKind.Scheduled, Note = "폭우" }, // 과거 예정 → 없음
                new DayMark { Date = new DateOnly(2026, 10, 10), Kind = DayKind.Scheduled },               // 미래 예정 → 불변
                new DayMark { Date = new DateOnly(2026, 9, 20), Kind = DayKind.NoActivity, Note = "시험" }); // 과거 없음 → 불변
            await db.SaveChangesAsync();
        }

        var rows = await host.AdminActivities.GetActivitiesAsync();

        var expired = rows.Single(r => r.Date == new DateOnly(2026, 9, 10));
        Assert.Equal(DayKind.NoActivity, expired.Kind);
        Assert.Equal("활동 없음(폭우)", expired.StateText);
        Assert.Equal("활동 예정", rows.Single(r => r.Date == new DateOnly(2026, 10, 10)).StateText);
        Assert.Equal("활동 없음(시험)", rows.Single(r => r.Date == new DateOnly(2026, 9, 20)).StateText);
        await using (var db = host.Db.CreateContext())
        {
            Assert.Equal(DayKind.NoActivity, (await db.DayMarks.SingleAsync(m => m.Date == new DateOnly(2026, 9, 10))).Kind);
            Assert.Equal(DayKind.Scheduled, (await db.DayMarks.SingleAsync(m => m.Date == new DateOnly(2026, 10, 10))).Kind);
            Assert.Equal(DayKind.NoActivity, (await db.DayMarks.SingleAsync(m => m.Date == new DateOnly(2026, 9, 20))).Kind);
            var sweepLog = await db.AuditLogs.AsNoTracking().SingleAsync(a => a.Action == AdminAction.ActivityAutoExpire);
            Assert.Equal("활동 없음 자동 전환 1건", sweepLog.Details);
        }

        await host.AdminActivities.GetActivitiesAsync(); // 재조회 — 멱등, 감사로그 추가 없음
        await using (var db = host.Db.CreateContext())
            Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.Action == AdminAction.ActivityAutoExpire));
    }

    [Fact]
    public async Task 탭명_학기가_없으면_첫_마지막_활동일_범위로_생성되고_기존_학기_날짜는_불변이다()
    {
        await using var host = new ServiceHost();
        host.AsAdmin();
        await host.SeedUserAsync("김민수", "2025");
        await host.Semester.CreateAsync(new SemesterSaveRequest("2026 고정", new(2026, 3, 1), new(2026, 7, 31)));

        using var stream = new MemoryStream(TestWorkbook.Write(("2027-1",
        [
            ["활동 상태", "활동 완료", "활동 완료"],
            ["총 인원 : ", "27. 03. 05. 금", "27. 03. 19. 금"],
            ["김민수 2025 체육", "출석", "출석"],
        ])));
        var report = await host.Archive.ImportAsync(stream);

        Assert.Equal(2, report.ImportedActivities);
        Assert.Empty(report.Warnings);
        await using var db = host.Db.CreateContext();
        var created = await db.SemesterPeriods.AsNoTracking().SingleAsync(p => p.Name == "2027-1");
        Assert.Equal(new DateOnly(2027, 3, 5), created.StartDate);
        Assert.Equal(new DateOnly(2027, 3, 19), created.EndDate);
        var existing = await db.SemesterPeriods.AsNoTracking().SingleAsync(p => p.Name == "2026 고정");
        Assert.Equal(new DateOnly(2026, 3, 1), existing.StartDate);
        Assert.Equal(new DateOnly(2026, 7, 31), existing.EndDate);
        Assert.Equal(2, await db.SemesterPeriods.CountAsync());
    }

    [Fact]
    public async Task 학기_종료일은_출석0인_완료_활동과_예정_활동까지_포함하고_없음은_제외한다()
    {
        await using var host = new ServiceHost();
        host.AsAdmin();
        await host.SeedUserAsync("김민수", "2025");

        // 완료(출석0) → 예정 → 없음 순의 열: 종료일은 예정 마지막 날(04-02)까지, 없음 날(04-09)은 제외
        using var stream = new MemoryStream(TestWorkbook.Write(("2027-1",
        [
            ["활동 상태", "활동 완료", "활동 완료", "활동 예정", "활동 없음(중간고사)"],
            ["총 인원 : ", "27. 03. 05. 금", "27. 03. 26. 금", "27. 04. 02. 금", "27. 04. 09. 금"],
            ["김민수 2025 체육", "출석", "", "", ""],
        ])));
        await host.Archive.ImportAsync(stream);

        await using var db = host.Db.CreateContext();
        var created = await db.SemesterPeriods.AsNoTracking().SingleAsync(p => p.Name == "2027-1");
        Assert.Equal(new DateOnly(2027, 3, 5), created.StartDate);
        Assert.Equal(new DateOnly(2027, 4, 2), created.EndDate);
    }

    [Fact]
    public async Task 같은_실행_내_같은_탭명은_학기를_중복_생성하지_않는다()
    {
        await using var host = new ServiceHost();
        host.AsAdmin();
        await host.SeedUserAsync("김민수", "2025");

        // 트림하면 같은 이름 — 첫 탭이 만든 학기를 둘째 탭이 이름 일치로 재사용한다
        using var stream = new MemoryStream(TestWorkbook.Write(
            ("2027-1",
            [
                ["활동 상태", "활동 완료"],
                ["총 인원 : ", "27. 03. 05. 금"],
                ["김민수 2025 체육", "출석"],
            ]),
            ("2027-1 ",
            [
                ["활동 상태", "활동 완료"],
                ["총 인원 : ", "27. 03. 19. 금"],
                ["김민수 2025 체육", "출석"],
            ])));
        await host.Archive.ImportAsync(stream);

        await using var db = host.Db.CreateContext();
        var period = await db.SemesterPeriods.AsNoTracking().SingleAsync(p => p.Name == "2027-1");
        Assert.Equal(1, await db.SemesterPeriods.CountAsync());
        Assert.Equal(1, await db.SemesterMembers.CountAsync(m => m.SemesterId == period.Id));
        Assert.Equal(2, await db.SemesterAttendances.CountAsync(a => a.SemesterMember.SemesterId == period.Id));
    }

    [Fact]
    public async Task 활동_수정_부원_목록은_학기_매핑_부원으로_한정되고_학기_밖_활동은_전체다()
    {
        await using var host = new ServiceHost();
        host.AsAdmin();
        var kim = await host.SeedUserAsync("김민수", "2025");
        var park = await host.SeedUserAsync("박지훈", "2026");
        var lee = await host.SeedUserAsync("이정우", "2027"); // 학기에 매핑되지 않은 부원
        var semester = await host.Semester.CreateAsync(new SemesterSaveRequest("2026-2", new(2026, 9, 1), new(2026, 12, 31)));
        var inSemester = await host.AdminActivities.CreateAsync(new ActivityCreateRequest(new(2026, 9, 15), DayKind.Completed));
        await using (var db = host.Db.CreateContext())
        {
            db.SemesterMembers.AddRange(
                new SemesterMember { SemesterId = semester.Id, Label = "김민수 2025", Name = "김민수", MappedUserId = kim, CreatedAt = DateTime.UtcNow },
                new SemesterMember { SemesterId = semester.Id, Label = "박지훈 2026", Name = "박지훈", MappedUserId = park, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        var scoped = await host.AdminActivities.GetMembersAsync(inSemester.Key!);
        Assert.Equal([kim, park], scoped.Select(m => m.UserId).OrderBy(x => x, StringComparer.Ordinal).ToList());

        // 학기 밖(미분류) 활동은 기존처럼 전체 활성 부원을 나열한다
        var outside = await host.AdminActivities.CreateAsync(new ActivityCreateRequest(new(2027, 1, 10), DayKind.Completed));
        var full = await host.AdminActivities.GetMembersAsync(outside.Key!);
        Assert.Equal(3, full.Count);
        Assert.Contains(full, m => m.UserId == lee);
    }
}
