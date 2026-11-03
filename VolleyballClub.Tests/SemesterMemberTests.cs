using Microsoft.EntityFrameworkCore;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Application.Exceptions;
using VolleyballClub.Domain.Enums;
using VolleyballClub.Infrastructure.Persistence;
using Xunit;

namespace VolleyballClub.Tests;

/// <summary>학기별 부원 행 관리 — 미분류 추가·중복 라벨·이중 집계 방지 회귀.</summary>
public class SemesterMemberTests
{
    [Fact]
    public async Task 미분류_행_추가가_된다()
    {
        await using var host = new ServiceHost();
        host.AsAdmin();

        var row = await host.SheetRows.AddRowAsync(new SemesterMemberCreateRequest(0, "졸업생", null));

        await using var db = host.Db.CreateContext();
        var stored = await db.SemesterMembers.SingleAsync(m => m.Id == row.Id);
        Assert.Null(stored.SemesterId);
        Assert.Equal("졸업생", stored.Label);
        Assert.Equal("졸업생", stored.Name);
        Assert.Null(stored.MappedUserId);
    }

    [Fact]
    public async Task 같은_학기_같은_라벨_행_추가는_거부된다()
    {
        await using var host = new ServiceHost();
        host.AsAdmin();
        await host.Semester.CreateAsync(new SemesterSaveRequest("2026-2", new(2026, 9, 1), new(2026, 12, 31)));
        var semester = await host.Db.CreateContext().SemesterPeriods.SingleAsync();

        await host.SheetRows.AddRowAsync(new SemesterMemberCreateRequest(semester.Id, "졸업생", null));
        await Assert.ThrowsAsync<ConflictException>(
            () => host.SheetRows.AddRowAsync(new SemesterMemberCreateRequest(semester.Id, "졸업생", null)));
        // 미분류(0)와는 라벨이 같아도 공존한다
        await host.SheetRows.AddRowAsync(new SemesterMemberCreateRequest(0, "졸업생", null));
    }

    [Fact]
    public async Task 다른_학기_행에_이미_있는_활동_출석_추가는_이중_집계를_막기_위해_거부된다()
    {
        await using var host = new ServiceHost();
        host.AsAdmin();
        var member = await host.SeedUserAsync("김민수", "2025");
        var activity = await host.SeedActivityAsync(new DateOnly(2026, 10, 1), ActivityStatus.ActivityClosed);

        // 미분류 행 2개를 같은 계정에 매핑(미분류는 다중 매핑 허용)
        var rowA = await host.SheetRows.AddRowAsync(new SemesterMemberCreateRequest(0, "행A", null));
        var rowB = await host.SheetRows.AddRowAsync(new SemesterMemberCreateRequest(0, "행B", null));
        await host.SheetRows.SetMappingAsync(rowA.Id, member);
        await host.SheetRows.SetMappingAsync(rowB.Id, member);

        await host.SheetRows.SetRowAttendanceAsync(rowA.Id, activity.Id, true);
        // 행B에 같은 활동을 추가하면 같은 계정으로 이중 집계된다 — 거부
        await Assert.ThrowsAsync<ConflictException>(
            () => host.SheetRows.SetRowAttendanceAsync(rowB.Id, activity.Id, true));
        // 행A의 출석을 취소하면 행B에 추가할 수 있다
        await host.SheetRows.SetRowAttendanceAsync(rowA.Id, activity.Id, false);
        await host.SheetRows.SetRowAttendanceAsync(rowB.Id, activity.Id, true);

        await using var db = host.Db.CreateContext();
        Assert.Equal(1, await db.SemesterAttendances.CountAsync(sa => sa.ActivityId == activity.Id));
    }
}
