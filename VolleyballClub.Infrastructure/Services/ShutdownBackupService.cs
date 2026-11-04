using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VolleyballClub.Application.Abstractions;

namespace VolleyballClub.Infrastructure.Services;

/// <summary>프로세스 종료(컨테이너 교체·재시작) 직전 DB 백업 — 배포 마이그레이션 전 스냅샷 확보.</summary>
// ponytail: ShutdownTimeout(기본 10초)·Watchtower 강제종료(기본 10초) 내 완료 가정 — DB 대형화 시 HostOptions.ShutdownTimeout과 WATCHTOWER_TIMEOUT을 함께 늘릴 것.
internal sealed class ShutdownBackupService(
    IServiceScopeFactory scopes,
    ILogger<ShutdownBackupService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IDatabaseBackupService>().RunShutdownBackupAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { logger.LogError(ex, "종료 전 DB 백업 실패 — 업데이트는 계속 진행된다"); }
    }
}
