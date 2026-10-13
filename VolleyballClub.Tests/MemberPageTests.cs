using Microsoft.EntityFrameworkCore;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Application.Exceptions;
using VolleyballClub.Domain.Entities;
using VolleyballClub.Domain.Enums;
using VolleyballClub.Infrastructure.Services;

namespace VolleyballClub.Tests;

/// <summary>부원 관리 페이지 개편 — 서버 페이징·필터·정렬, 상태 모델, 부원 상세 출석 기록 정정.</summary>
public class MemberPageTests
{
    // 기본 시각 2026-10-01(KST) → 이번 달 10월, 학기 기본 규칙 9/1~12/31, 현재 학기 탭 '2026-2'
    private static ServiceHost NewHost() =>
        new ServiceHost(formatResolver: new SheetFormatResolver(SemesterTabFormat.All())).AsAdmin();

    private static async Task SeedAttendanceRowAsync(ServiceHost host, int activityId, string userId)
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

    private static async Task SeedMappingAsync(ServiceHost host, int sheetId, string userId)
    {
        await using var db = host.Db.CreateContext();
        db.SheetMemberMappings.Add(new SheetMemberMapping
        {
            SheetDefinitionId = sheetId,
            TabName = "2026-2",
            RowLabel = $"{userId} 행",
            UserId = userId,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task 페이징_25명을_10개씩_조회하면_3페이지에_5명이다()
    {
        var host = NewHost();
        for (var i = 1; i <= 25; i++)
        {
            await host.SeedUserAsync($"부원{i:D2}", $"2025{i:D5}");
        }

        var page1 = await host.Member.GetPageAsync(new MemberPageRequest(Page: 1));
        var page3 = await host.Member.GetPageAsync(new MemberPageRequest(Page: 3));

        Assert.Equal(25, page1.TotalCount);
        Assert.Equal(10, page1.Items.Count);
        Assert.Equal(5, page3.Items.Count);
    }

    [Fact]
    public async Task 상태필터_복수선택하면_해당_상태만_반환한다()
    {
        var host = NewHost();
        await host.SeedUserAsync("활동부원", "2001", MemberStatus.Active);
        await host.SeedUserAsync("탈퇴부원", "2002", MemberStatus.Withdrawn);
        await host.SeedUserAsync("퇴출부원", "2003", MemberStatus.Expelled);

        var both = await host.Member.GetPageAsync(new MemberPageRequest(
            Statuses: [MemberStatus.Active, MemberStatus.Withdrawn]));
        var expelled = await host.Member.GetPageAsync(new MemberPageRequest(
            Statuses: [MemberStatus.Expelled]));

        Assert.Equal(2, both.TotalCount);
        Assert.DoesNotContain(both.Items, m => m.Status == MemberStatus.Expelled);
        Assert.Single(expelled.Items);
        Assert.Equal(MemberStatus.Expelled, expelled.Items[0].Status);
    }

    [Fact]
    public async Task 시트연결_회비_필터가_서버에서_동작한다()
    {
        var host = NewHost();
        var mapped = await host.SeedUserAsync("김연결", "3001");
        var unmapped = await host.SeedUserAsync("이미연", "3002");
        var sheetId = await SeedAttendanceSheetAsync(host);
        await SeedMappingAsync(host, sheetId, mapped);

        var period = await host.Fee.CreatePeriodAsync(new FeePeriodCreateRequest(
            "2026-2학기", new DateOnly(2026, 9, 1), new DateOnly(2026, 12, 31), 10000));
        await host.Fee.SetPaidAsync(period.Id, mapped, isPaid: true);

        var sheetMapped = await host.Member.GetPageAsync(new MemberPageRequest(SheetMapped: true));
        var sheetUnmapped = await host.Member.GetPageAsync(new MemberPageRequest(SheetMapped: false));
        var feeUnpaid = await host.Member.GetPageAsync(new MemberPageRequest(FeePaid: false));
        var feePaid = await host.Member.GetPageAsync(new MemberPageRequest(FeePaid: true));

        Assert.Equal([mapped], sheetMapped.Items.Select(m => m.UserId));
        Assert.Equal([unmapped], sheetUnmapped.Items.Select(m => m.UserId));
        Assert.Equal([unmapped], feeUnpaid.Items.Select(m => m.UserId));
        Assert.Equal([mapped], feePaid.Items.Select(m => m.UserId));
    }

    [Fact]
    public async Task 출석집계_학기_월_총_3종이_구분되고_오름내림_정렬된다()
    {
        var host = NewHost();
        var busy = await host.SeedUserAsync("가바쁨", "4001");
        var some = await host.SeedUserAsync("나적당", "4002");
        var none = await host.SeedUserAsync("다없음", "4003");

        // 학기 내·10월 내 2건 + 학기 내(9월) 1건 + 학기 밖(8월) 1건 = 총 4
        var oct = await host.SeedActivityAsync(new DateOnly(2026, 10, 1));
        var oct2 = await host.SeedActivityAsync(new DateOnly(2026, 10, 2));
        var sep = await host.SeedActivityAsync(new DateOnly(2026, 9, 20), ActivityStatus.ActivityClosed);
        var aug = await host.SeedActivityAsync(new DateOnly(2026, 8, 15), ActivityStatus.ActivityClosed);

        foreach (var activity in new[] { oct.Id, oct2.Id, sep.Id, aug.Id })
        {
            await SeedAttendanceRowAsync(host, activity, busy);
        }
        await SeedAttendanceRowAsync(host, sep.Id, some);

        var desc = await host.Member.GetPageAsync(new MemberPageRequest(
            Sort: MemberSortField.TotalAttendance, Ascending: false));
        var asc = await host.Member.GetPageAsync(new MemberPageRequest(
            Sort: MemberSortField.MonthAttendance, Ascending: true));

        var busyRow = desc.Items.Single(m => m.UserId == busy);
        Assert.Equal(4, busyRow.TotalAttendance);
        Assert.Equal(3, busyRow.SemesterAttendance);
        Assert.Equal(2, busyRow.MonthAttendance);

        Assert.Equal([busy, some, none], desc.Items.Select(m => m.UserId)); // 총 출석 내림: 4, 1, 0
        Assert.Equal([some, none, busy], asc.Items.Select(m => m.UserId)); // 10월 출석 오름: 0, 0(이름순), 2
    }

    [Fact]
    public async Task 상태변경_탈퇴처리하면_DB에_Withdrawn으로_저장된다()
    {
        var host = NewHost();
        var member = await host.SeedUserAsync("김탈퇴", "5001");

        await host.Member.UpdateAsync(member, new MemberUpdateRequest("김탈퇴", "체육학과", Status: MemberStatus.Withdrawn));

        await using var db = host.Db.CreateContext();
        var user = await db.Users.SingleAsync(u => u.Id == member);
        Assert.Equal(MemberStatus.Withdrawn, user.Status);
    }

    [Fact]
    public async Task 과거_마감활동에_출석을_추가하고_중복은_거부된다()
    {
        var host = NewHost();
        var member = await host.SeedUserAsync("김정정", "6001");
        var activity = await host.SeedActivityAsync(new DateOnly(2026, 9, 20), ActivityStatus.ActivityClosed);

        await host.Attendance.AdminAddForActivityAsync(member, activity.Id);

        await Assert.ThrowsAsync<ConflictException>(
            () => host.Attendance.AdminAddForActivityAsync(member, activity.Id));

        var records = await host.Attendance.GetAdminRecordsAsync(member);
        Assert.Equal(1, records.TotalCount);
        Assert.Single(records.Records);
        Assert.Equal(new DateOnly(2026, 9, 20), records.Records[0].ActivityDate);
    }

    [Fact]
    public async Task 출석삭제_팀편성이_있으면_확인이_필요하고_팀에서도_제외된다()
    {
        var host = NewHost();
        var member = await host.SeedUserAsync("김삭제", "7001");
        var activity = await host.SeedActivityAsync(new DateOnly(2026, 9, 25), ActivityStatus.ActivityClosed);
        await SeedAttendanceRowAsync(host, activity.Id, member);
        await using (var db = host.Db.CreateContext())
        {
            var team = new Team { ActivityId = activity.Id, Name = "A팀", Order = 1, CreatedAt = DateTime.UtcNow };
            db.Teams.Add(team);
            await db.SaveChangesAsync();
            db.TeamMembers.Add(new TeamMember { TeamId = team.Id, UserId = member, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<ConflictException>(
            () => host.Attendance.AdminRemoveForActivityAsync(member, activity.Id));
        await host.Attendance.AdminRemoveForActivityAsync(member, activity.Id, confirmTeamImpact: true);

        await using var verify = host.Db.CreateContext();
        Assert.Empty(await verify.Attendances.Where(a => a.UserId == member).ToListAsync());
        Assert.Empty(await verify.TeamMembers.Where(m => m.UserId == member).ToListAsync());
    }

    [Fact]
    public async Task 출석기록조회_없는_부원이면_NotFound다()
    {
        var host = NewHost();
        await Assert.ThrowsAsync<NotFoundException>(() => host.Attendance.GetAdminRecordsAsync("없는사람"));
    }
}
