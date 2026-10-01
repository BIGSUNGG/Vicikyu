using Microsoft.EntityFrameworkCore;
using VolleyballClub.Application.Exceptions;
using VolleyballClub.Domain.Enums;
using Xunit;

namespace VolleyballClub.Tests;

/// <summary>OTP 출석 — 정상 출석, 잘못된 OTP, 중복 출석 거부.</summary>
public class OtpAttendanceTests : IAsyncLifetime
{
    private readonly ServiceHost host = new();

    public async Task InitializeAsync()
    {
        await host.SeedUserAsync("김민수", "202512001");
        host.AsAdmin();
        await host.Activity.GetOrCreateTodayAsync();
        await host.Activity.GenerateOtpAsync();
        await host.Activity.StartAttendanceAsync();
        host.AsMember("user-202512001");
    }

    public Task DisposeAsync() => host.DisposeAsync().AsTask();

    private async Task<string> GetOtpAsync()
    {
        host.AsAdmin();
        var today = await host.Activity.GetTodayForAdminAsync();
        var otp = today.OtpCode!;
        host.AsMember("user-202512001");
        return otp;
    }

    [Fact]
    public async Task CorrectOtp_CreatesAttendance()
    {
        var otp = await GetOtpAsync();

        var checkedAt = await host.Attendance.CheckInWithOtpAsync(otp);

        Assert.Equal(1, await host.CountAttendancesAsync());
        var activity = await host.GetTodayActivityAsync();
        await using var db = host.Db.CreateContext();
        var attendance = await db.Attendances.SingleAsync(a => a.ActivityId == activity.Id && a.UserId == "user-202512001");
        Assert.Equal(AttendanceType.Otp, attendance.AttendanceType);
        Assert.Equal(checkedAt, attendance.CheckedAt, precision: TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task WrongOtp_ThrowsRuleViolation()
    {
        var otp = await GetOtpAsync();
        var wrong = otp == "9999" ? "0000" : "9999";

        var ex = await Assert.ThrowsAsync<RuleViolationException>(() => host.Attendance.CheckInWithOtpAsync(wrong));
        Assert.Contains("OTP", ex.Message);
        Assert.Equal(0, await host.CountAttendancesAsync());
    }

    [Fact]
    public async Task DuplicateCheckIn_ThrowsConflict()
    {
        var otp = await GetOtpAsync();
        await host.Attendance.CheckInWithOtpAsync(otp);

        var ex = await Assert.ThrowsAsync<ConflictException>(() => host.Attendance.CheckInWithOtpAsync(otp));

        Assert.Contains("이미 출석", ex.Message);
        Assert.Equal(1, await host.CountAttendancesAsync());
    }
}
