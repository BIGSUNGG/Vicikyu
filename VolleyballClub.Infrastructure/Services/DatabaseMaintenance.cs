using VolleyballClub.Application.Abstractions;

namespace VolleyballClub.Infrastructure.Services;

internal sealed class DatabaseMaintenance(string connectionString) : IDatabaseMaintenance
{
    private readonly string recoveryMarker = RecoveryMarkerPath(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource);
    internal static string RecoveryMarkerPath(string databasePath) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(databasePath))!, "database-backups", "restore-recovery-required.txt");
    private readonly SemaphoreSlim turnstile = new(1, 1);
    private readonly SemaphoreSlim room = new(1, 1);
    private readonly SemaphoreSlim readersMutex = new(1, 1);
    private int readers;
    private volatile bool blocked;
    public bool IsBlocked => blocked || File.Exists(recoveryMarker);
    public void Block() => blocked = true;

    public async Task<IDisposable> EnterAsync(bool exclusive, CancellationToken ct = default)
    {
        await turnstile.WaitAsync(ct);
        if (exclusive)
        {
            try { await room.WaitAsync(ct); }
            catch { turnstile.Release(); throw; }
            return new Lease(() => { room.Release(); turnstile.Release(); });
        }
        turnstile.Release();
        await readersMutex.WaitAsync(ct);
        try
        {
            if (readers == 0) await room.WaitAsync(ct);
            readers++;
        }
        finally { readersMutex.Release(); }
        return new Lease(() =>
        {
            readersMutex.Wait();
            try { if (--readers == 0) room.Release(); }
            finally { readersMutex.Release(); }
        });
    }

    private sealed class Lease(Action release) : IDisposable
    {
        private Action? release = release;
        public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
    }
}
