namespace VolleyballClub.Application.Abstractions;

public interface IDatabaseBackupService
{
    Task<Stream> ExportAsync(CancellationToken ct = default);
    /// <summary>Caller must hold an exclusive maintenance lease and drain earlier database scopes before restoring.</summary>
    Task RestoreAsync(Stream file, CancellationToken ct = default);
}

public static class DatabaseBackupLimits
{
    public const long MaxFileBytes = 64 * 1024 * 1024;
}

/// <summary>HTTP scope lifetime coordination: restore is exclusive; ordinary requests may overlap.</summary>
public interface IDatabaseMaintenance
{
    Task<IDisposable> EnterAsync(bool exclusive, CancellationToken ct = default);
    bool IsBlocked { get; }
    void Block();
}
