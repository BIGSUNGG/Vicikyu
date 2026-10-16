using System.Buffers.Binary;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VolleyballClub.Application.Abstractions;
using VolleyballClub.Application.Exceptions;
using VolleyballClub.Domain.Enums;
using VolleyballClub.Infrastructure.Persistence;

namespace VolleyballClub.Infrastructure.Services;

// Internal native-I/O boundary permits failure/recovery checks without exposing test hooks in public contracts.
internal interface IDatabaseSnapshot
{
    void Copy(SqliteConnection source, SqliteConnection destination);
}
internal sealed class SqliteDatabaseSnapshot : IDatabaseSnapshot
{
    public void Copy(SqliteConnection source, SqliteConnection destination) => source.BackupDatabase(destination);
}

internal sealed class DatabaseBackupService(
    IDbContextFactory<ApplicationDbContext> factory,
    ICurrentUserService current,
    IDatabaseMaintenance maintenance,
    IDatabaseSnapshot snapshot,
    ILogger<DatabaseBackupService> logger) : IDatabaseBackupService
{
    private static SqliteConnection Connect(string path, SqliteOpenMode mode) => new(new SqliteConnectionStringBuilder
    {
        DataSource = path, Mode = mode, Pooling = false, DefaultTimeout = 30
    }.ToString());

    private async Task<string> DatabasePathAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        var builder = new SqliteConnectionStringBuilder(connection.ConnectionString);
        if (builder.Mode == SqliteOpenMode.Memory || builder.DataSource is "" or ":memory:")
            throw new RuleViolationException("파일 기반 SQLite DB에서만 백업을 사용할 수 있습니다.");
        await connection.OpenAsync(ct);
        return Path.GetFullPath(connection.DataSource);
    }

    private static void PrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    private static void PrivateFile(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var file = new FileStream(path, options);
    }

    public async Task<Stream> ExportAsync(CancellationToken ct = default)
    {
        await current.RequireAdminAsync();
        var path = Path.Combine(Path.GetDirectoryName(await DatabasePathAsync(ct))!, $".database-export-{Guid.NewGuid():N}.db");
        try
        {
            PrivateFile(path);
            await using (var db = await factory.CreateDbContextAsync(ct))
            {
                var source = (SqliteConnection)db.Database.GetDbConnection();
                await source.OpenAsync(ct);
                await using var destination = Connect(path, SqliteOpenMode.ReadWrite);
                await destination.OpenAsync(ct);
                snapshot.Copy(source, destination); // SQLite online backup includes committed WAL pages.
                await MakeStandaloneAsync(destination, ct);
            }
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
                FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        }
        catch { File.Delete(path); throw; }
    }

    public async Task RestoreAsync(Stream file, CancellationToken ct = default)
    {
        await current.RequireAdminAsync();
        var livePath = await DatabasePathAsync(ct);
        var work = Path.Combine(Path.GetDirectoryName(livePath)!, $".database-restore-{Guid.NewGuid():N}");
        PrivateDirectory(work);
        try
        {
            var uploadedPath = Path.Combine(work, "uploaded.db");
            PrivateFile(uploadedPath);
            await using (var output = new FileStream(uploadedPath, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[65536];
                long total = 0;
                int read;
                while ((read = await file.ReadAsync(buffer, ct)) != 0)
                {
                    total += read;
                    if (total > DatabaseBackupLimits.MaxFileBytes) throw new RuleViolationException("DB 파일은 64MB 이하여야 합니다.");
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                await output.FlushAsync(ct);
                output.Flush(flushToDisk: true);
                if (total == 0) throw new RuleViolationException("빈 DB 파일은 복원할 수 없습니다.");
            }
            await ValidateAsync(uploadedPath, Path.Combine(work, "expected.db"), ct);
            // Rotate on the staging DB: even a crash immediately after atomic replacement cannot revive old cookies.
            await using (var staged = Connect(uploadedPath, SqliteOpenMode.ReadWrite))
            {
                await staged.OpenAsync(ct);
                await using var stamps = staged.CreateCommand();
                stamps.CommandText = "UPDATE AspNetUsers SET SecurityStamp = lower(hex(randomblob(32))), ConcurrencyStamp = lower(hex(randomblob(16)))";
                await stamps.ExecuteNonQueryAsync(ct);
            }
            ct.ThrowIfCancellationRequested();

            var backupDirectory = Path.Combine(Path.GetDirectoryName(livePath)!, "database-backups");
            PrivateDirectory(backupDirectory);
            var backupPath = Path.Combine(backupDirectory, $"before-restore-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.db");
            PrivateFile(backupPath);
            await using var live = Connect(livePath, SqliteOpenMode.ReadWrite);
            await live.OpenAsync(ct);
            try
            {
                await using (var backup = Connect(backupPath, SqliteOpenMode.ReadWrite))
                {
                    await backup.OpenAsync(ct);
                    snapshot.Copy(live, backup); // No live mutation unless the safety backup succeeds.
                    await MakeStandaloneAsync(backup, ct);
                }
                using var durableBackup = new FileStream(backupPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                durableBackup.Flush(flushToDisk: true);
            }
            catch { File.Delete(backupPath); throw; }
            logger.LogInformation("DB pre-restore backup retained at {BackupPath}", backupPath);
            var marker = DatabaseMaintenance.RecoveryMarkerPath(livePath);
            PrivateFile(marker);
            using (var markerFile = new FileStream(marker, FileMode.Open, FileAccess.Write, FileShare.Read))
            {
                markerFile.Write(System.Text.Encoding.UTF8.GetBytes(backupPath));
                markerFile.Flush(flushToDisk: true);
            }

            // The exclusive HTTP lease drains and disposes every earlier auth/business scope first.
            // SQLite's backup API replaces database pages atomically; no main-file rename or stale WAL sidecars.
            // Cancellation is deliberately ignored from this point through recovery.
            try
            {
                await using var uploaded = Connect(uploadedPath, SqliteOpenMode.ReadOnly);
                await uploaded.OpenAsync(CancellationToken.None);
                snapshot.Copy(uploaded, live);
                await CheckIntegrityAsync(live, CancellationToken.None);
                File.Delete(marker);
            }
            catch (Exception restoreError)
            {
                try
                {
                    await using var backup = Connect(backupPath, SqliteOpenMode.ReadOnly);
                    await backup.OpenAsync(CancellationToken.None);
                    snapshot.Copy(backup, live);
                    await CheckIntegrityAsync(live, CancellationToken.None);
                    File.Delete(marker);
                }
                catch (Exception recoveryError)
                {
                    maintenance.Block();
                    logger.LogCritical(recoveryError, "DB recovery failed; all HTTP traffic blocked. Retained backup: {BackupPath}", backupPath);
                    throw new AggregateException("DB 복원과 원본 복구에 실패했습니다. 서비스를 차단했습니다. 운영자가 자동 백업으로 복구해야 합니다.", restoreError, recoveryError);
                }
                logger.LogError(restoreError, "DB restore failed; original database recovered from {BackupPath}", backupPath);
                throw new RuleViolationException("DB 복원에 실패하여 기존 DB로 복구했습니다. 자동 백업은 서버에 보관됩니다.");
            }
        }
        catch (SqliteException ex)
        {
            logger.LogWarning(ex, "DB backup file rejected or inaccessible");
            throw new RuleViolationException("DB 파일이 손상되었거나 읽을 수 없습니다. 현재 앱에서 내보낸 백업을 사용해 주세요.");
        }
        finally { Directory.Delete(work, recursive: true); }
    }

    private static async Task ValidateAsync(string uploadedPath, string expectedPath, CancellationToken ct)
    {
        await using var uploaded = Connect(uploadedPath, SqliteOpenMode.ReadOnly);
        await uploaded.OpenAsync(ct);
        await CheckIntegrityAsync(uploaded, ct);
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(new SqliteConnectionStringBuilder
        {
            DataSource = expectedPath, Pooling = false
        }.ToString()).Options;
        await using var expectedDb = new ApplicationDbContext(options);
        await expectedDb.Database.MigrateAsync(ct);
        var expected = (SqliteConnection)expectedDb.Database.GetDbConnection();
        await expected.OpenAsync(ct);
        if (!(await SchemaAsync(uploaded, ct)).SequenceEqual(await SchemaAsync(expected, ct)) ||
            !(await HistoryAsync(uploaded, ct)).SequenceEqual(await HistoryAsync(expected, ct)))
            throw new RuleViolationException("현재 앱과 동일한 DB 스키마·버전만 복원할 수 있습니다.");

        await using var command = uploaded.CreateCommand();
        command.CommandText = """
            SELECT u.UserName, u.NormalizedUserName, u.PasswordHash FROM AspNetUsers u
            JOIN AspNetUserRoles ur ON ur.UserId = u.Id JOIN AspNetRoles r ON r.Id = ur.RoleId
            WHERE r.Name = 'ADMIN' AND r.NormalizedName = 'ADMIN' AND u.Status = $active
              AND u.TwoFactorEnabled = 0 AND (u.LockoutEnabled = 0 OR u.LockoutEnd IS NULL OR julianday(u.LockoutEnd) <= julianday('now'))
            """;
        command.Parameters.AddWithValue("$active", (int)MemberStatus.Active);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (!reader.IsDBNull(0) && !reader.IsDBNull(1) && !reader.IsDBNull(2) &&
                !string.IsNullOrWhiteSpace(reader.GetString(0)) && reader.GetString(0) == reader.GetString(0).Trim() &&
                reader.GetString(1) == new UpperInvariantLookupNormalizer().NormalizeName(reader.GetString(0)) &&
                UsablePasswordHash(reader.GetString(2))) return;
        }
        throw new RuleViolationException("로그인 가능한 활성 관리자 계정이 없는 DB는 복원할 수 없습니다.");
    }

    private static bool UsablePasswordHash(string hash)
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(hash); }
        catch (FormatException) { return false; }
        if (bytes.Length == 49 && bytes[0] == 0) return true;
        if (bytes.Length < 13 || bytes[0] != 1) return false;
        var prf = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(1, 4));
        var iterations = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(5, 4));
        var salt = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(9, 4));
        return prf <= 2 && iterations is > 0 and <= 1_000_000 && salt >= 16 && salt <= bytes.Length - 29;
    }

    private static async Task MakeStandaloneAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=DELETE";
        if (!string.Equals((await command.ExecuteScalarAsync(ct))?.ToString(), "delete", StringComparison.OrdinalIgnoreCase))
            throw new IOException("독립 DB 백업 파일을 만들지 못했습니다.");
    }

    private static async Task CheckIntegrityAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check";
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct) || reader.GetString(0) != "ok" || await reader.ReadAsync(ct))
                throw new RuleViolationException("무결성 검사에 실패한 DB는 복원할 수 없습니다.");
        }
        command.CommandText = "PRAGMA foreign_key_check";
        await using var foreignKeys = await command.ExecuteReaderAsync(ct);
        if (await foreignKeys.ReadAsync(ct)) throw new RuleViolationException("참조 관계가 손상된 DB는 복원할 수 없습니다.");
    }

    private static Task<List<string>> SchemaAsync(SqliteConnection connection, CancellationToken ct) => RowsAsync(connection,
        "SELECT type || char(31) || name || char(31) || tbl_name || char(31) || coalesce(sql, '') FROM sqlite_master WHERE name NOT GLOB 'sqlite_*' ORDER BY type, name", ct);
    private static Task<List<string>> HistoryAsync(SqliteConnection connection, CancellationToken ct) => RowsAsync(connection,
        // EF patch metadata can differ for the same applied migrations; it is not an app schema version.
        "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId", ct);
    private static async Task<List<string>> RowsAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(ct);
        List<string> rows = [];
        while (await reader.ReadAsync(ct)) rows.Add(reader.GetString(0));
        return rows;
    }
}
