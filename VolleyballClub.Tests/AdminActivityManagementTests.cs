using Microsoft.EntityFrameworkCore;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Application.Exceptions;
using VolleyballClub.Domain.Entities;
using VolleyballClub.Domain.Enums;
using VolleyballClub.Infrastructure.Identity;
using VolleyballClub.Infrastructure.Services;

namespace VolleyballClub.Tests;

/// <summary>관리자 활동 관리(DB 단일 원천) — DayMark(예정/없음)와 Activity(완료/진행)를 한 목록으로 다룬다.
/// Key 규약: DayMark는 "mark:{yyyy-MM-dd}", Activity는 "app:{id}".</summary>
public sealed partial class AdminActivityManagementTests
{
    private sealed class Host : IAsyncDisposable
    {
        public TestDb Db { get; } = new();
        public FakeCurrentUser Current { get; } = new("admin", true);
        public FixedTimeProvider Clock { get; } = new(new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc));
        public AdminActivityService Service { get; }

        public Host()
        {
            Service = new(Db.Factory, Current, Clock);
        }

        /// <summary>부원 3명(admin/a/unmapped) + 과거 기록 3종(완료 활동·예정 마크·없음 마크)을 시드한다.</summary>
        public async Task Seed()
        {
            await using var db = Db.CreateContext();
            foreach (var id in new[] { "admin", "a", "unmapped" })
                db.Users.Add(new ApplicationUser
                {
                    Id = id, UserName = id, NormalizedUserName = id.ToUpperInvariant(),
                    StudentNumber = id, Name = id, Status = MemberStatus.Active,
                });
            await db.SaveChangesAsync();
            await Service.CreateAsync(new ActivityCreateRequest(new(2026, 9, 1), DayKind.Completed));
            await Service.CreateAsync(new ActivityCreateRequest(new(2026, 9, 8), DayKind.Scheduled));
            await Service.CreateAsync(new ActivityCreateRequest(new(2026, 9, 15), DayKind.NoActivity, Note: "시험"));
        }

        /// <summary>테스트용 출석 시드 — (user, 활동 학기) 매핑 행을 보장하고 학기별 출석을 추가한다.</summary>
        public async Task SeedAttendanceAsync(string userId, int activityId, AttendanceType type = AttendanceType.Admin)
        {
            await using var db = Db.CreateContext();
            var activity = await db.Activities.AsNoTracking().SingleAsync(a => a.Id == activityId);
            var now = DateTime.UtcNow;
            var row = await SemesterMemberRows.EnsureUserRowAsync(db, userId, activity, now);
            db.SemesterAttendances.Add(new SemesterAttendance
            {
                SemesterMember = row,
                ActivityId = activityId,
                AttendanceType = type,
                CheckedAt = now,
                CreatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        public Task<IReadOnlyList<ManagedActivityDto>> List() => Service.GetActivitiesAsync();

        public async Task<ManagedActivityDto> Completed() => (await List()).Single(a => a.Kind == DayKind.Completed);

        public async Task<Activity> App(DateOnly date, ActivityStatus status = ActivityStatus.ActivityClosed)
        {
            await using var db = Db.CreateContext();
            var a = new Activity { ActivityDate = date, Status = status, CreatedByUserId = "admin", OtpCode = "1234" };
            db.Activities.Add(a); await db.SaveChangesAsync(); return a;
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    [Fact]
    public async Task Ready_app_activity_completes_when_kind_set_to_completed()
    {
        await using var h = new Host();
        await h.Seed();
        var ready = await h.App(new(2026, 10, 1), ActivityStatus.Ready);

        var dto = await h.Service.EditAsync($"app:{ready.Id}", new ActivityEditRequest(ready.ActivityDate, DayKind.Completed));

        await using var db = h.Db.CreateContext();
        var stored = await db.Activities.SingleAsync(a => a.Id == ready.Id);
        Assert.Equal(ActivityStatus.ActivityClosed, stored.Status);
        Assert.NotNull(stored.ActivityClosedAt);
        Assert.Equal(ActivityStatus.ActivityClosed, dto.Status);
    }

    [Fact]
    public async Task Ready_app_activity_completes_and_sets_attendance_in_one_batch_save()
    {
        await using var h = new Host();
        await h.Seed();
        var ready = await h.App(new(2026, 10, 1), ActivityStatus.Ready);

        // 화면 흐름 그대로 — 완료 선택 + 출석 체크를 변경 저장 1회로
        var dto = await h.Service.EditAsync($"app:{ready.Id}", new ActivityEditRequest(ready.ActivityDate, DayKind.Completed,
            Attendances: [new ActivityAttendanceSetRequest("a", true)]));

        await using var db = h.Db.CreateContext();
        var stored = await db.Activities.Include(a => a.SemesterAttendances).ThenInclude(a => a.SemesterMember).SingleAsync(a => a.Id == ready.Id);
        Assert.Equal(ActivityStatus.ActivityClosed, stored.Status);
        Assert.Single(stored.SemesterAttendances, x => x.SemesterMember.MappedUserId == "a");
    }

    [Fact]
    public async Task Ready_app_activity_converts_to_scheduled_mark()
    {
        await using var h = new Host();
        await h.Seed();
        var ready = await h.App(new(2026, 10, 1), ActivityStatus.Ready);

        var dto = await h.Service.EditAsync($"app:{ready.Id}", new ActivityEditRequest(ready.ActivityDate, DayKind.Scheduled));

        await using var db = h.Db.CreateContext();
        Assert.Equal(ActivityStatus.Ready, ready.Status); // 시드 엔티티는 그대로(컨텍스트 분리)
        Assert.DoesNotContain(await db.Activities.ToListAsync(), a => a.Id == ready.Id); // 활동은 제거
        var mark = await db.DayMarks.SingleAsync(m => m.Date == new DateOnly(2026, 10, 1));
        Assert.Equal(DayKind.Scheduled, mark.Kind);
        Assert.Null(dto.ActivityId); // 마크 행으로 전환됐다
    }

    [Fact]
    public async Task All_operations_require_admin()
    {
        await using var h = new Host(); h.Current.IsAdmin = false;
        var date = new DateOnly(2026, 10, 2);
        Func<Task>[] calls = [() => h.List(),
            () => h.Service.CreateAsync(new(date, DayKind.Scheduled)),
            () => h.Service.EditAsync("x", new(date)), () => h.Service.DeleteAsync("x"),
            () => h.Service.GetMembersAsync("x"), () => h.Service.SetAttendanceAsync("x", "a", new(true)),
            () => h.Service.GetMemberActivitiesAsync("a")];
        foreach (var call in calls) await Assert.ThrowsAsync<ForbiddenException>(call);
        await using var db = h.Db.CreateContext();
        Assert.Empty(await db.Activities.ToListAsync());
        Assert.Empty(await db.DayMarks.ToListAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("장소 휴관")]
    public async Task Create_none_reason_round_trips_and_scheduled_never_becomes_current(string? reason)
    {
        await using var h = new Host(); await h.Seed();
        var date = new DateOnly(2026, 10, 2);
        var none = await h.Service.CreateAsync(new(date, DayKind.NoActivity, Note: reason));
        var same = await h.Service.CreateAsync(new(date, DayKind.NoActivity, Note: reason));
        Assert.Equal(none.Key, same.Key);
        var scheduled = await h.Service.CreateAsync(new(date.AddDays(1), DayKind.Scheduled));
        Assert.Null(scheduled.ActivityId);
        Assert.Equal(reason, (await h.List()).Single(r => r.Key == none.Key).Note);
        await using var db = h.Db.CreateContext();
        // 마크는 Activity를 만들지 않는다 — Seed의 완료 활동만 존재
        Assert.Single(await db.Activities.ToListAsync());
        // 예정/없음 표시에는 출석이 없다(활동 키가 아니므로 찾을 수 없음)
        await Assert.ThrowsAsync<NotFoundException>(() => h.Service.SetAttendanceAsync(none.Key, "a", new(true)));
        await Assert.ThrowsAsync<NotFoundException>(() => h.Service.SetAttendanceAsync(scheduled.Key, "a", new(true)));
    }

    [Fact]
    public async Task Attendance_date_edits_preserve_identity_attendance_and_member_rows()
    {
        await using var h = new Host(); await h.Seed(); var row = await h.Completed();
        await h.Service.SetAttendanceAsync(row.Key, "a", new(true));
        // 직접 심은 출석도 편집·재시도에서 보존된다
        await h.SeedAttendanceAsync("unmapped", row.ActivityId!.Value, AttendanceType.Admin);
        await h.Service.SetAttendanceAsync(row.Key, "a", new(true)); // 동일 요청 재시도 — 멱등
        // 출석자가 있는 활동은 예정/없음으로 바꿀 수 없다
        await Assert.ThrowsAsync<ConflictException>(() => h.Service.EditAsync(row.Key, new(row.Date, DayKind.NoActivity)));
        await using (var db = h.Db.CreateContext()) Assert.Equal(2, await db.SemesterAttendances.CountAsync());
        var edited = await h.Service.EditAsync(row.Key, new(new(2026, 9, 2)));
        var resynced = (await h.List()).Single(r => r.Key == row.Key);
        Assert.Equal(edited, resynced); Assert.Equal(row.ActivityId, edited.ActivityId); Assert.Equal(2, edited.AttendanceCount);
        var member = await h.Service.GetMemberActivitiesAsync("unmapped");
        Assert.Equal(1, member.TotalCount); Assert.Equal(1, member.SemesterCount); Assert.Equal(0, member.MonthCount);
        // 실제 출석한 활동 행만 나타난다(미귀속 학기의 미출석 행 제외)
        Assert.Single(member.Items); Assert.True(member.Items.Single(r => r.Key == row.Key).IsAttended);
        await using var verify = h.Db.CreateContext();
        Assert.All(await verify.SemesterAttendances.ToListAsync(), a => Assert.Equal(AttendanceType.Admin, a.AttendanceType));
    }

    [Fact]
    public async Task Team_removal_needs_confirmation_and_admin_endpoint_shares_the_same_path()
    {
        await using var h = new Host(); await h.Seed(); var row = await h.Completed();
        var attendance = new AttendanceService(h.Db.Factory, h.Current, new AuditLogService(h.Db.Factory, h.Current, h.Clock),
            h.Clock, h.Service);
        await attendance.AdminAddForActivityAsync("a", row.ActivityId!.Value);
        await using (var db = h.Db.CreateContext())
        {
            var team = new Team { ActivityId = row.ActivityId.Value, Name = "A" }; team.Members.Add(new TeamMember { UserId = "a" });
            db.Teams.Add(team); await db.SaveChangesAsync();
        }
        var details = await h.Service.GetMemberActivitiesAsync("a");
        Assert.True(details.Items.Single(r => r.Key == row.Key).HasTeam);
        await Assert.ThrowsAsync<ConflictException>(() => attendance.AdminRemoveForActivityAsync("a", row.ActivityId.Value));
        await attendance.AdminRemoveForActivityAsync("a", row.ActivityId.Value, true);
        await using var verify = h.Db.CreateContext();
        Assert.Empty(await verify.SemesterAttendances.ToListAsync()); Assert.Empty(await verify.TeamMembers.ToListAsync());
        Assert.Equal(2, await verify.AuditLogs.CountAsync(a =>
            a.Action == AdminAction.AttendanceAdd || a.Action == AdminAction.AttendanceRemove));
    }

    [Fact]
    public async Task Delete_activity_cascades_only_activity_dependents_and_is_retry_safe()
    {
        await using var h = new Host(); await h.Seed(); var row = await h.Completed();
        await h.Service.SetAttendanceAsync(row.Key, "a", new(true));
        await using (var db = h.Db.CreateContext())
        {
            db.Guests.Add(new Guest { ActivityId = row.ActivityId!.Value, Name = "합성 게스트" });
            var team = new Team { ActivityId = row.ActivityId.Value, Name = "A" }; team.Members.Add(new TeamMember { UserId = "a" });
            db.Teams.Add(team);
            db.SemesterPeriods.Add(new SemesterPeriod { Name = "보존", StartDate = new(2026, 8, 1), EndDate = new(2027, 1, 31) });
            await db.SaveChangesAsync();
        }
        await h.Service.DeleteAsync(row.Key);
        await Assert.ThrowsAsync<NotFoundException>(() => h.Service.DeleteAsync(row.Key)); // 앱 키 재시도는 NotFound
        Assert.Equal(2, (await h.List()).Count); // 예정·없음 마크만 남는다
        await using var verify = h.Db.CreateContext();
        Assert.Equal(3, await verify.Users.CountAsync()); Assert.Equal(1, await verify.SemesterPeriods.CountAsync());
        Assert.Contains(await verify.AuditLogs.ToListAsync(), a => a.Action == AdminAction.ActivityCancel);
        Assert.Empty(await verify.Activities.ToListAsync());
        Assert.Empty(await verify.SemesterAttendances.ToListAsync()); Assert.Empty(await verify.Guests.ToListAsync());
        Assert.Empty(await verify.Teams.ToListAsync()); Assert.Empty(await verify.TeamMembers.ToListAsync());
    }

    [Fact]
    public async Task Ready_app_activity_rejects_attendance_until_started()
    {
        await using var h = new Host(); await h.Seed();
        var app = await h.App(new(2026, 10, 1), ActivityStatus.Ready);
        await Assert.ThrowsAsync<ConflictException>(() => h.Service.SetAttendanceAsync($"app:{app.Id}", "a", new(true)));
        await using var db = h.Db.CreateContext();
        Assert.Empty(await db.SemesterAttendances.ToListAsync());
    }
}
