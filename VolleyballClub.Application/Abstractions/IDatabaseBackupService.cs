namespace VolleyballClub.Application.Abstractions;

public interface IDatabaseBackupService
{
    Task<Stream> ExportAsync(CancellationToken ct = default);
    /// <summary>Caller must hold an exclusive maintenance lease and drain earlier database scopes before restoring.</summary>
    Task RestoreAsync(Stream file, CancellationToken ct = default);

    /// <summary>자동 백업 보관 기간(일). 설정이 없으면 기본값.</summary>
    Task<int> GetRetentionDaysAsync(CancellationToken ct = default);

    /// <summary>자동 백업 보관 기간(일) 변경 — 관리자만.</summary>
    Task SetRetentionDaysAsync(int days, CancellationToken ct = default);

    /// <summary>지금 시점 자동 백업 1건 생성 + 보관 기간 지난 자동 백업 정리. 당일 중복 실행은 건너뛴다(스케줄러 전용, 권한 검사 없음).</summary>
    Task RunScheduledBackupAsync(CancellationToken ct = default);
}

public static class DatabaseBackupLimits
{
    public const long MaxFileBytes = 64 * 1024 * 1024;
    public const string RetentionSettingKey = "Backup:RetentionDays";
    public const int DefaultRetentionDays = 30;
    public const int MinRetentionDays = 1;
    public const int MaxRetentionDays = 3650;
}

/// <summary>HTTP scope lifetime coordination: restore is exclusive; ordinary requests may overlap.</summary>
public interface IDatabaseMaintenance
{
    Task<IDisposable> EnterAsync(bool exclusive, CancellationToken ct = default);
    bool IsBlocked { get; }
    void Block();
}
