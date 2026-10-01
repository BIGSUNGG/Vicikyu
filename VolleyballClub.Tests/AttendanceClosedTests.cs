using VolleyballClub.Application.Exceptions;
using Xunit;

namespace VolleyballClub.Tests;

/// <summary>출석 마감/활동 종료 이후의 출석 거부와 Activity 부재 처리.</summary>
public class AttendanceClosedTests : IAsyncLifetime
{
    private readonly ServiceHost host = new();

    public async Task InitializeAsync()
    {
        await host.SeedUserAsync("박지훈", "202512002");
    }

    public Task DisposeAsync() => host.DisposeAsync().AsTask();

    [Fact]
    public async Task ClosedAttendance_RejectsOtpCheckIn()
    {
        host.AsAdmin();
        await host.Activity.GetOrCreateTodayAsync();
        await host.Activity.GenerateOtpAsync();
        await host.Activity.StartAttendanceAsync();
        await host.Activity.CloseAttendanceAsync();
        var otp = (await host.Activity.GetTodayForAdminAsync()).OtpCode!;

        host.AsMember("user-202512002");
        var ex = await Assert.ThrowsAsync<ConflictException>(() => host.Attendance.CheckInWithOtpAsync(otp));
        Assert.Contains("출석이 이미 마감", ex.Message);
        Assert.Equal(0, await host.CountAttendancesAsync());
    }

    [Fact]
    public async Task ActivityClosed_RejectsOtpCheckIn()
    {
        host.AsAdmin();
        await host.Activity.GetOrCreateTodayAsync();
        await host.Activity.GenerateOtpAsync();
        await host.Activity.StartAttendanceAsync();
        await host.Activity.CloseAttendanceAsync();
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
        await host.Activity.StartAttendanceAsync();
        await host.Activity.CloseActivityAsync();

        var ex = await Assert.ThrowsAsync<ConflictException>(
            () => host.Attendance.AdminAddAsync("user-202512002"));
        Assert.Contains("종료", ex.Message);
    }
}
