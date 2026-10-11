using VolleyballClub.Application.Exceptions;
using VolleyballClub.Domain.Enums;
using Xunit;

namespace VolleyballClub.Tests;

/// <summary>활동 상태별 OTP 출석 허용/거부 정책 — 활동 중·팀 확정 상태에서는 상시 출석.</summary>
public class AttendancePolicyTests : IAsyncLifetime
{
    private readonly ServiceHost host = new();

    public async Task InitializeAsync()
    {
        await host.SeedUserAsync("박지훈", "202512002");
    }

    public Task DisposeAsync() => host.DisposeAsync().AsTask();

    [Fact]
    public async Task ActiveActivity_AllowsOtpCheckIn()
    {
        host.AsAdmin();
        await host.Activity.GetOrCreateTodayAsync();
        await host.Activity.GenerateOtpAsync();
        await host.Activity.StartActivityAsync();
        var otp = (await host.Activity.GetTodayForAdminAsync()).OtpCode!;

        host.AsMember("user-202512002");
        await host.Attendance.CheckInWithOtpAsync(otp);
        Assert.Equal(1, await host.CountAttendancesAsync());
    }

    [Fact]
    public async Task TeamCreatedActivity_AllowsOtpCheckIn()
    {
        host.AsAdmin();
        await host.SeedActivityAsync(Domain.KstClock.Today(host.Time), ActivityStatus.TeamCreated, otp: "1234");

        host.AsMember("user-202512002");
        await host.Attendance.CheckInWithOtpAsync("1234");
        Assert.Equal(1, await host.CountAttendancesAsync());
    }

    [Fact]
    public async Task ReadyActivity_RejectsOtpCheckIn()
    {
        host.AsAdmin();
        await host.Activity.GetOrCreateTodayAsync();
        await host.Activity.GenerateOtpAsync();

        host.AsMember("user-202512002");
        var ex = await Assert.ThrowsAsync<ConflictException>(() => host.Attendance.CheckInWithOtpAsync("1234"));
        Assert.Contains("아직 시작", ex.Message);
        Assert.Equal(0, await host.CountAttendancesAsync());
    }

    [Fact]
    public async Task ActivityClosed_RejectsOtpCheckIn()
    {
        host.AsAdmin();
        await host.Activity.GetOrCreateTodayAsync();
        await host.Activity.GenerateOtpAsync();
        await host.Activity.StartActivityAsync();
        await host.Activity.CloseActivityAsync();
        var otp = (await host.Activity.GetTodayForAdminAsync()).OtpCode!;

        host.AsMember("user-202512002");
        var ex = await Assert.ThrowsAsync<ConflictException>(() => host.Attendance.CheckInWithOtpAsync(otp));
        Assert.Contains("활동이 종료", ex.Message);
    }

    [Fact]
    public async Task NoActivity_ThrowsNotFound()
    {
        host.AsMember("user-202512002");
        await Assert.ThrowsAsync<NotFoundException>(() => host.Attendance.CheckInWithOtpAsync("1234"));
    }

    [Fact]
    public async Task ClosedActivity_RejectsAdminAttendanceChange()
    {
        host.AsAdmin();
        await host.Activity.GetOrCreateTodayAsync();
        await host.Activity.StartActivityAsync();
        await host.Activity.CloseActivityAsync();

        var ex = await Assert.ThrowsAsync<ConflictException>(
            () => host.Attendance.AdminAddAsync("user-202512002"));
        Assert.Contains("종료", ex.Message);
    }
}
