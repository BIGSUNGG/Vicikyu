using Microsoft.EntityFrameworkCore;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Domain.Entities;
using VolleyballClub.Domain.Enums;
using Xunit;

namespace VolleyballClub.Tests;

/// <summary>기록 페이지용 월간 이력 조회 — 활동 전체(상태 포함)와 내 출석.</summary>
public class RecordsTests
{
    private static async Task<ServiceHost> NewHostAsync()
    {
        var host = new ServiceHost();
        host.AsAdmin();
        // 2026-2 학기 등록 — 귀속 학기의 활동이 기록에 나타난다
        await host.Semester.CreateAsync(new SemesterSaveRequest("2026년 2학기", new(2026, 9, 1), new(2026, 12, 31)));
        return host;
    }

    [Fact]
    public async Task Records_ListsAllMonthActivities_LatestFirst_WithEvents()
    {
        await using var host = await NewHostAsync();
        var alice = await host.SeedUserAsync("김민수", "202512001");

        // 다른 달 활동은 제외
        await host.SeedActivityAsync(new DateOnly(2026, 9, 30), ActivityStatus.ActivityClosed);

        // 10/1: 팀 편성 완료 — alice 출석
        var oct1 = await host.SeedActivityAsync(new DateOnly(2026, 10, 1), ActivityStatus.TeamCreated);
        await AddAttendanceDirectAsync(host, oct1.Id, alice);

        // 10/2: 종료된 활동 — alice 미출석
        await host.SeedActivityAsync(new DateOnly(2026, 10, 2), ActivityStatus.ActivityClosed);

        var records = await host.Attendance.GetMyRecordsAsync(alice, 2026, 10);

        Assert.Equal(2, records.MonthActivities);
        Assert.Equal(1, records.MonthCount);
        Assert.Equal(2, records.Records.Count);
        Assert.Equal(new DateOnly(2026, 10, 2), records.Records[0].Date); // 최신순
        Assert.Equal(ActivityStatus.ActivityClosed, records.Records[0].Status);
        Assert.False(records.Records[0].IsAttended);

        var first = records.Records[1];
        Assert.Equal(new DateOnly(2026, 10, 1), first.Date);
        Assert.Equal(ActivityStatus.TeamCreated, first.Status);
        Assert.True(first.IsAttended);
        Assert.NotNull(first.CheckedAt);
    }

    [Fact]
    public async Task Records_AreScopedToRequestingUser()
    {
        await using var host = await NewHostAsync();
        var alice = await host.SeedUserAsync("김민수", "202512001");
        var bob = await host.SeedUserAsync("박지훈", "202512002");

        var activity = await host.SeedActivityAsync(new DateOnly(2026, 10, 1), ActivityStatus.ActivityClosed);
        await AddAttendanceDirectAsync(host, activity.Id, bob);

        var mine = await host.Attendance.GetMyRecordsAsync(alice, 2026, 10);
        var bobs = await host.Attendance.GetMyRecordsAsync(bob, 2026, 10);

        // alice는 이 학기에 귀속되지 않았다(가입일은 3월, 출석 없음) — 남의 출석이 보이지 않는다
        Assert.Empty(mine.Records);
        Assert.Equal(0, mine.MonthCount);

        Assert.True(bobs.Records.Single().IsAttended);
        Assert.Equal(1, bobs.MonthCount);
    }

    private static async Task AddAttendanceDirectAsync(ServiceHost host, int activityId, string userId)
    {
        await using var db = host.Db.CreateContext();
        db.Attendances.Add(new Attendance
        {
            ActivityId = activityId,
            UserId = userId,
            AttendanceType = AttendanceType.Otp,
            CheckedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }
}
