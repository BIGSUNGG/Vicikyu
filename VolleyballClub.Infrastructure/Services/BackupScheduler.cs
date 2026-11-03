using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VolleyballClub.Application.Abstractions;

namespace VolleyballClub.Infrastructure.Services;

/// <summary>서버 실행 중 매일 오후 12시(서버 로컬 시간) 자동 DB 백업. 정오 이후 기동이면 당일 백업을 즉시 따라잡는다.</summary>
internal sealed class BackupScheduler(
    IServiceScopeFactory scopes,
    TimeProvider timeProvider,
    ILogger<BackupScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan Noon = TimeSpan.FromHours(12);

    /// <summary>마지막 실행일 대비 현재 시각의 실행 계획: 지금 실행할지, 아니면 얼마나 기다릴지.</summary>
    internal static (bool RunNow, TimeSpan Wait) Plan(DateTimeOffset localNow, DateOnly lastRun)
    {
        var todayNoon = localNow.Date + Noon;
        if (lastRun == DateOnly.FromDateTime(localNow.DateTime)) return (false, todayNoon.AddDays(1) - localNow);
        if (localNow >= todayNoon) return (true, TimeSpan.Zero);
        return (false, todayNoon - localNow);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastRun = DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime).AddDays(-1);
        while (!stoppingToken.IsCancellationRequested)
        {
            var (runNow, wait) = Plan(timeProvider.GetLocalNow(), lastRun);
            if (!runNow && wait > TimeSpan.Zero)
            {
                try { await Task.Delay(wait, stoppingToken); }
                catch (OperationCanceledException) { return; }
            }
            await RunAsync(stoppingToken);
            lastRun = DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IDatabaseBackupService>().RunScheduledBackupAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { logger.LogError(ex, "DB 자동 백업 실행 실패"); }
    }
}
