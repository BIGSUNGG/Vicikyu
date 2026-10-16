using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VolleyballClub.Application.Abstractions;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Domain.Entities;
using VolleyballClub.Domain.Enums;
using VolleyballClub.Infrastructure.Identity;
using VolleyballClub.Infrastructure.Persistence;
using VolleyballClub.Infrastructure.Services;

namespace VolleyballClub.Tests;

public sealed class DatabaseBackupTests
{
    [Fact]
    public async Task Export_Includes_Committed_Wal_Pages_In_One_Standalone_Db()
    {
        await using var host = await BackupHost.StartAsync();
        using var admin = host.Client(); await Login(admin, "admin");
        await using var writer = new SqliteConnection(host.Connection);
        await writer.OpenAsync();
        await Sql(writer, "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; INSERT INTO Settings (Key, Value, UpdatedAt) VALUES ('wal-only', 'committed', '2026-01-01');");
        Assert.True(new FileInfo(host.DbPath + "-wal").Length > 0);
        var bytes = await admin.GetByteArrayAsync("/api/admin/database/export");
        var standalone = Path.Combine(host.DirectoryPath, "standalone.db");
        await File.WriteAllBytesAsync(standalone, bytes);
        await using var exported = new SqliteConnection($"Data Source={standalone};Mode=ReadOnly;Pooling=False");
        await exported.OpenAsync();
        Assert.Equal("committed", await Scalar(exported, "SELECT Value FROM Settings WHERE Key='wal-only'"));
        Assert.Equal("ok", await Scalar(exported, "PRAGMA integrity_check"));
        Assert.False(File.Exists(standalone + "-wal"));
    }

    [Fact]
    public async Task Whole_Restore_Replaces_Identity_Settings_Business_And_Revokes_All_Old_Cookies()
    {
        await using var host = await BackupHost.StartAsync();
        using var admin = host.Client(); using var otherAdmin = host.Client(); using var member = host.Client();
        await Login(admin, "admin"); await Login(otherAdmin, "admin"); await Login(member, "member");
        var originalStamp = await host.Query("SELECT SecurityStamp FROM AspNetUsers WHERE Id='admin-id'");
        var bytes = await admin.GetByteArrayAsync("/api/admin/database/export");
        await host.Execute("UPDATE Settings SET Value='changed'; DELETE FROM Activities;");
        using (var scope = host.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            Assert.True((await users.ChangePasswordAsync((await users.FindByNameAsync("member"))!, "pass", "changed")).Succeeded);
            Assert.True((await users.CreateAsync(new ApplicationUser { UserName = "after-backup", StudentNumber = "after-backup", Name = "synthetic" }, "pass")).Succeeded);
        }
        using (var form = Upload(bytes)) await Status(admin.PostAsync("/api/admin/database/import", form), HttpStatusCode.NoContent);
        Assert.Equal("synthetic-secret", await host.Query("SELECT Value FROM Settings WHERE Key='Google:ServiceAccountJson'"));
        Assert.Equal("1", await host.Query("SELECT COUNT(*) FROM Activities"));
        Assert.Equal("0", await host.Query("SELECT COUNT(*) FROM AspNetUsers WHERE UserName='after-backup'"));
        Assert.NotEqual(originalStamp, await host.Query("SELECT SecurityStamp FROM AspNetUsers WHERE Id='admin-id'"));
        await Status(otherAdmin.GetAsync("/api/auth/user"), HttpStatusCode.Unauthorized);
        await Status(member.GetAsync("/api/auth/user"), HttpStatusCode.Unauthorized);
        await Status(admin.GetAsync("/api/auth/user"), HttpStatusCode.Unauthorized);
        await Login(admin, "admin"); await Login(member, "member");
        await Status(admin.GetAsync("/api/admin/database/export"), HttpStatusCode.OK);
        var backups = System.IO.Directory.GetFiles(Path.Combine(host.DirectoryPath, "database-backups"), "*.db");
        Assert.Single(backups);
        await using var backup = new SqliteConnection($"Data Source={backups[0]};Mode=ReadOnly;Pooling=False");
        await backup.OpenAsync();
        Assert.Equal("changed", await Scalar(backup, "SELECT Value FROM Settings LIMIT 1"));
        Assert.Equal(originalStamp, await Scalar(backup, "SELECT SecurityStamp FROM AspNetUsers WHERE Id='admin-id'"));
        Assert.False(host.Services.GetRequiredService<IDatabaseMaintenance>().IsBlocked);
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("foreign-app")]
    [InlineData("extra-trigger")]
    [InlineData("disguised-sqlite-trigger")]
    [InlineData("missing-column")]
    [InlineData("old-history")]
    [InlineData("future-history")]
    [InlineData("no-admin")]
    [InlineData("inactive-admin")]
    [InlineData("locked-admin")]
    [InlineData("two-factor-admin")]
    [InlineData("bad-password-hash")]
    [InlineData("bad-normalized-name")]
    [InlineData("unloggable-trimmed-name")]
    [InlineData("foreign-key")]
    public async Task Invalid_Corrupt_Schema_Or_Unusable_Admin_File_Is_Rejected_Without_Changing_Original(string problem)
    {
        await using var host = await BackupHost.StartAsync();
        using var admin = host.Client(); await Login(admin, "admin");
        var stamp = await host.Query("SELECT SecurityStamp FROM AspNetUsers WHERE Id='admin-id'");
        var bytes = await admin.GetByteArrayAsync("/api/admin/database/export");
        if (problem == "corrupt") bytes = [1, 2, 3, 4];
        else
        {
            var path = Path.Combine(host.DirectoryPath, "invalid.db");
            await File.WriteAllBytesAsync(path, bytes);
            await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                await connection.OpenAsync();
                await Sql(connection, problem switch
                {
                    "foreign-app" => "CREATE TABLE ForeignApp(Id INTEGER PRIMARY KEY)",
                    "extra-trigger" => "CREATE TRIGGER injected AFTER UPDATE ON Settings BEGIN DELETE FROM Settings; END",
                    "disguised-sqlite-trigger" => "CREATE TRIGGER sqliteXpayload AFTER UPDATE ON AspNetUsers BEGIN DELETE FROM AspNetUserRoles; END",
                    "missing-column" => "ALTER TABLE Settings DROP COLUMN Value",
                    "old-history" => "DELETE FROM __EFMigrationsHistory WHERE MigrationId=(SELECT MAX(MigrationId) FROM __EFMigrationsHistory)",
                    "future-history" => "INSERT INTO __EFMigrationsHistory VALUES ('999999_Future','10.0.12')",
                    "no-admin" => "DELETE FROM AspNetUserRoles WHERE UserId='admin-id'",
                    "inactive-admin" => "UPDATE AspNetUsers SET Status=1 WHERE Id='admin-id'",
                    "locked-admin" => "UPDATE AspNetUsers SET LockoutEnabled=1, LockoutEnd='2999-01-01T00:00:00+00:00' WHERE Id='admin-id'",
                    "two-factor-admin" => "UPDATE AspNetUsers SET TwoFactorEnabled=1 WHERE Id='admin-id'",
                    "bad-password-hash" => "UPDATE AspNetUsers SET PasswordHash='bad hash' WHERE Id='admin-id'",
                    "bad-normalized-name" => "UPDATE AspNetUsers SET NormalizedUserName='WRONG' WHERE Id='admin-id'",
                    "unloggable-trimmed-name" => "UPDATE AspNetUsers SET UserName=' admin ', NormalizedUserName=' ADMIN ' WHERE Id='admin-id'",
                    "foreign-key" => "PRAGMA foreign_keys=OFF; INSERT INTO AspNetUserRoles VALUES ('missing-user','missing-role')",
                    _ => throw new ArgumentOutOfRangeException(nameof(problem))
                });
            }
            bytes = await File.ReadAllBytesAsync(path);
        }
        using (var form = Upload(bytes)) await Status(admin.PostAsync("/api/admin/database/import", form), HttpStatusCode.BadRequest);
        Assert.Equal("synthetic-secret", await host.Query("SELECT Value FROM Settings LIMIT 1"));
        Assert.Equal(stamp, await host.Query("SELECT SecurityStamp FROM AspNetUsers WHERE Id='admin-id'"));
        await Status(admin.GetAsync("/api/auth/user"), HttpStatusCode.OK);
        Assert.False(System.IO.Directory.Exists(Path.Combine(host.DirectoryPath, "database-backups")));
        Assert.Empty(System.IO.Directory.GetDirectories(host.DirectoryPath, ".database-restore-*"));
    }

    [Fact]
    public async Task Same_Current_Schema_With_Different_Ef_Patch_Metadata_Is_Restorable()
    {
        await using var host = await BackupHost.StartAsync();
        using var admin = host.Client(); await Login(admin, "admin");
        var bytes = await admin.GetByteArrayAsync("/api/admin/database/export");
        var path = Path.Combine(host.DirectoryPath, "same-schema.db");
        await File.WriteAllBytesAsync(path, bytes);
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            await Sql(connection, "UPDATE __EFMigrationsHistory SET ProductVersion='10.0.1'");
        }
        using var form = Upload(await File.ReadAllBytesAsync(path));
        await Status(admin.PostAsync("/api/admin/database/import", form), HttpStatusCode.NoContent);
        Assert.Equal("synthetic-secret", await host.Query("SELECT Value FROM Settings LIMIT 1"));
    }

    [Fact]
    public async Task Admin_Authorization_Csrf_Confirmation_Extension_And_Upload_Size_Are_Enforced()
    {
        await using var host = await BackupHost.StartAsync();
        using var anonymous = host.Client(); using var member = host.Client(); using var admin = host.Client();
        await Token(anonymous);
        await Status(anonymous.GetAsync("/api/admin/database/export"), HttpStatusCode.Unauthorized);
        using (var form = Upload([1])) await Status(anonymous.PostAsync("/api/admin/database/import", form), HttpStatusCode.Unauthorized);
        await Login(member, "member");
        await Status(member.GetAsync("/api/admin/database/export"), HttpStatusCode.Forbidden);
        using (var form = Upload([1])) await Status(member.PostAsync("/api/admin/database/import", form), HttpStatusCode.Forbidden);
        await Login(admin, "admin");
        var bytes = await admin.GetByteArrayAsync("/api/admin/database/export");
        using (var form = Upload(bytes, confirm: "NO")) await Status(admin.PostAsync("/api/admin/database/import", form), HttpStatusCode.BadRequest);
        using (var form = Upload(bytes, name: "backup.txt")) await Status(admin.PostAsync("/api/admin/database/import", form), HttpStatusCode.BadRequest);
        using (var form = Upload([])) await Status(admin.PostAsync("/api/admin/database/import", form), HttpStatusCode.BadRequest);
        using (var oversized = new DeclaredOversizedContent()) await Status(admin.PostAsync("/api/admin/database/import", oversized), HttpStatusCode.BadRequest);
        using (var form = Upload(new byte[checked((int)DatabaseBackupLimits.MaxFileBytes + 1)]))
            await Status(admin.PostAsync("/api/admin/database/import", form), HttpStatusCode.BadRequest);
        admin.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        using (var form = Upload(bytes)) await Status(admin.PostAsync("/api/admin/database/import", form), HttpStatusCode.BadRequest);
        Assert.Equal("synthetic-secret", await host.Query("SELECT Value FROM Settings LIMIT 1"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replacement_Failure_Rolls_Back_And_Recovery_Failure_Fails_Closed_Across_Restart(bool failRecovery)
    {
        var copy = new FailingSnapshot();
        await using var host = await BackupHost.StartAsync(copy);
        using var admin = host.Client(); await Login(admin, "admin");
        var bytes = await admin.GetByteArrayAsync("/api/admin/database/export");
        await host.Execute("UPDATE Settings SET Value='original-live';");
        copy.Calls = 0; copy.FailReplacement = true; copy.FailRecovery = failRecovery;
        using (var form = Upload(bytes)) await Status(admin.PostAsync("/api/admin/database/import", form), failRecovery ? HttpStatusCode.InternalServerError : HttpStatusCode.BadRequest);
        Assert.Equal(3, copy.Calls);
        var maintenance = host.Services.GetRequiredService<IDatabaseMaintenance>();
        Assert.Equal(failRecovery, maintenance.IsBlocked);
        Assert.Equal(failRecovery, new DatabaseMaintenance(host.Connection).IsBlocked);
        if (failRecovery)
        {
            await Status(admin.GetAsync("/api/auth/user"), HttpStatusCode.ServiceUnavailable);
            await Status(admin.GetAsync("/admin/settings"), HttpStatusCode.ServiceUnavailable);
            Assert.True(File.Exists(DatabaseMaintenance.RecoveryMarkerPath(host.DbPath)));
        }
        else
        {
            Assert.Equal("original-live", await host.Query("SELECT Value FROM Settings LIMIT 1"));
            await Status(admin.GetAsync("/api/auth/user"), HttpStatusCode.OK);
        }
        Assert.Single(System.IO.Directory.GetFiles(Path.Combine(host.DirectoryPath, "database-backups"), "*.db"));
    }

    [Fact]
    public async Task Safety_Backup_Failure_Leaves_Original_Unchanged_And_Does_Not_Start_Replacement()
    {
        var copy = new FailingSnapshot();
        await using var host = await BackupHost.StartAsync(copy);
        using var admin = host.Client(); await Login(admin, "admin");
        var bytes = await admin.GetByteArrayAsync("/api/admin/database/export");
        copy.Calls = 0; copy.FailBackup = true;
        using (var form = Upload(bytes)) await Status(admin.PostAsync("/api/admin/database/import", form), HttpStatusCode.InternalServerError);
        Assert.Equal(1, copy.Calls);
        Assert.Equal("synthetic-secret", await host.Query("SELECT Value FROM Settings LIMIT 1"));
        Assert.False(host.Services.GetRequiredService<IDatabaseMaintenance>().IsBlocked);
        Assert.Empty(System.IO.Directory.GetFiles(Path.Combine(host.DirectoryPath, "database-backups")));
        await Status(admin.GetAsync("/api/auth/user"), HttpStatusCode.OK);
    }

    [Fact]
    public async Task Exclusive_Restore_Drains_Existing_Work_And_Blocks_New_Requests_Before_Auth()
    {
        await using var host = await BackupHost.StartAsync(observeGate: true);
        using var admin = host.Client(); using var otherAdmin = host.Client();
        await Login(admin, "admin"); await Login(otherAdmin, "admin");
        var bytes = await admin.GetByteArrayAsync("/api/admin/database/export");
        var existing = admin.PostAsync("/api/admin/sheets/resync", null);
        await host.PausedSync.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            // Both casing and trailing-slash aliases must acquire the same exclusive endpoint lease.
            using var form = Upload(bytes);
            var restore = admin.PostAsync("/API/ADMIN/DATABASE/IMPORT/", form);
            await host.ObservedGate!.WriterRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var gate = host.Services.GetRequiredService<IDatabaseMaintenance>();
            var queuedReader = gate.EnterAsync(false);
            var auth = otherAdmin.GetAsync("/api/auth/user");
            var shell = otherAdmin.GetAsync("/admin/settings");
            Assert.False(restore.IsCompleted); Assert.False(queuedReader.IsCompleted);
            Assert.False(auth.IsCompleted); Assert.False(shell.IsCompleted);
            host.PausedSync.Resume.TrySetResult();
            await Status(existing.WaitAsync(TimeSpan.FromSeconds(10)), HttpStatusCode.OK);
            await Status(restore.WaitAsync(TimeSpan.FromSeconds(10)), HttpStatusCode.NoContent);
            using var queuedLease = await queuedReader.WaitAsync(TimeSpan.FromSeconds(10));
            // Authentication must occur after the writer, not accept an old stamp and then wait.
            await Status(auth.WaitAsync(TimeSpan.FromSeconds(10)), HttpStatusCode.Unauthorized);
            await Status(shell.WaitAsync(TimeSpan.FromSeconds(10)), HttpStatusCode.OK);
            var path = Assert.Single(System.IO.Directory.GetFiles(Path.Combine(host.DirectoryPath, "database-backups"), "*.db"));
            await using var backup = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            await backup.OpenAsync();
            Assert.Equal("1", await Scalar(backup, $"SELECT COUNT(*) FROM AuditLogs WHERE Action={(int)AdminAction.SheetResync}"));
        }
        finally { host.PausedSync.Resume.TrySetResult(); }
    }

    [Fact]
    public async Task Cancellation_Of_Waiting_Request_Does_Not_Leak_Maintenance_Lease()
    {
        await using var host = await BackupHost.StartAsync();
        var gate = host.Services.GetRequiredService<IDatabaseMaintenance>();
        using var first = await gate.EnterAsync(false);
        using var cts = new CancellationTokenSource();
        var waiting = gate.EnterAsync(true, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        first.Dispose();
        using var next = await gate.EnterAsync(true).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Cancellation_After_Replacement_Cannot_Interrupt_Completion_Or_Leave_Recovery_Marker()
    {
        var copy = new FailingSnapshot();
        await using var host = await BackupHost.StartAsync(copy);
        using var admin = host.Client(); await Login(admin, "admin");
        var bytes = await admin.GetByteArrayAsync("/api/admin/database/export");
        await host.Execute("UPDATE Settings SET Value='changed';");
        using var cts = new CancellationTokenSource();
        copy.Calls = 0; copy.AfterReplacement = cts.Cancel;
        using var lease = await host.Services.GetRequiredService<IDatabaseMaintenance>().EnterAsync(true);
        using var scope = host.Services.CreateScope();
        var current = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>();
        var previous = current.HttpContext;
        current.HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
        {
            User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                [new(System.Security.Claims.ClaimTypes.NameIdentifier, "admin-id"), new(System.Security.Claims.ClaimTypes.Role, "ADMIN")], "synthetic"))
        };
        try
        {
            await using var file = new MemoryStream(bytes);
            await scope.ServiceProvider.GetRequiredService<IDatabaseBackupService>().RestoreAsync(file, cts.Token);
        }
        finally { current.HttpContext = previous; }
        Assert.True(cts.IsCancellationRequested);
        Assert.Equal("synthetic-secret", await host.Query("SELECT Value FROM Settings LIMIT 1"));
        Assert.False(host.Services.GetRequiredService<IDatabaseMaintenance>().IsBlocked);
    }

    [Fact]
    public async Task Development_Migrations_Endpoint_Drains_With_Restore_And_Respects_Fail_Closed()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"vicikyu-migrations-gate-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(directory);
        var connection = $"Data Source={Path.Combine(directory, "live.db")};Pooling=False";
        var maintenance = new ObservedMaintenance(new DatabaseMaintenance(connection));
        try
        {
            await using var host = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.UseSetting("ConnectionStrings:DefaultConnection", connection);
                builder.UseSetting("Seed:AdminPassword", "synthetic-test-password");
                builder.UseSetting("Sheets:SyncOnStartup", "false");
                builder.UseSetting("Google:ServiceAccountJson", "");
                builder.UseSetting("Google:SpreadsheetId", "");
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IDatabaseMaintenance>();
                    services.AddSingleton<IDatabaseMaintenance>(maintenance);
                });
            });
            using var client = host.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            static FormUrlEncodedContent MigrationsForm() => new(new Dictionary<string, string>
            {
                ["context"] = typeof(ApplicationDbContext).AssemblyQualifiedName!
            });
            using var exclusive = await maintenance.EnterAsync(true);
            using var form = MigrationsForm();
            var queued = client.PostAsync("/ApplyDatabaseMigrations", form);
            await maintenance.ReaderRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(queued.IsCompleted);
            exclusive.Dispose();
            await Status(queued.WaitAsync(TimeSpan.FromSeconds(10)), HttpStatusCode.NoContent);
            maintenance.Block();
            using var blockedForm = MigrationsForm();
            await Status(client.PostAsync("/ApplyDatabaseMigrations", blockedForm), HttpStatusCode.ServiceUnavailable);
        }
        finally { System.IO.Directory.Delete(directory, recursive: true); }
    }

    private sealed class FailingSnapshot : IDatabaseSnapshot
    {
        public int Calls;
        public bool FailReplacement, FailRecovery, FailBackup;
        public Action? AfterReplacement;
        public void Copy(SqliteConnection source, SqliteConnection destination)
        {
            var call = ++Calls;
            if (call == 1 && FailBackup || call == 3 && FailRecovery) throw new IOException("synthetic native I/O failure");
            source.BackupDatabase(destination);
            if (call == 2) AfterReplacement?.Invoke();
            if (call == 2 && FailReplacement) throw new IOException("synthetic failure after replacement");
        }
    }

    private sealed class ObservedMaintenance(IDatabaseMaintenance inner) : IDatabaseMaintenance
    {
        public TaskCompletionSource WriterRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReaderRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsBlocked => inner.IsBlocked;
        public void Block() => inner.Block();
        public Task<IDisposable> EnterAsync(bool exclusive, CancellationToken ct = default)
        {
            var entering = inner.EnterAsync(exclusive, ct);
            if (exclusive) WriterRequested.TrySetResult();
            else ReaderRequested.TrySetResult();
            return entering;
        }
    }
    private sealed class PausingSheetSync : ISheetSyncService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<SheetSyncReport> ResyncAsync(CancellationToken ct = default)
        {
            Started.TrySetResult(); await Resume.Task.WaitAsync(ct);
            return new(0, 0, 0, 0, []);
        }
        public Task<SheetSyncReport> SyncAsync(CancellationToken ct = default) => ResyncAsync(ct);
        public Task<bool> ExportClosedActivityAsync(int id, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> ExportScheduledActivityAsync(int id, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> RemoveCancelledActivityAsync(int id, CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class DeclaredOversizedContent : HttpContent
    {
        public DeclaredOversizedContent() => Headers.ContentType = new("multipart/form-data") { Parameters = { new("boundary", "synthetic") } };
        protected override bool TryComputeLength(out long length) { length = DatabaseBackupLimits.MaxFileBytes + 65537; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;
    }

    private static MultipartFormDataContent Upload(byte[] bytes, string name = "synthetic.db", string confirm = "RESTORE")
    {
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(bytes), "file", name); form.Add(new StringContent(confirm), "confirm");
        return form;
    }
    private static async Task Login(HttpClient client, string user)
    {
        await Token(client);
        await Status(client.PostAsJsonAsync("/api/auth/login", new LoginRequest(user, "pass")), HttpStatusCode.NoContent);
        await Token(client);
    }
    private static async Task Token(HttpClient client)
    {
        var token = (await client.GetFromJsonAsync<CsrfTokenDto>("/api/auth/csrf"))!.Token;
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN"); client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token);
    }
    private static async Task Status(Task<HttpResponseMessage> task, HttpStatusCode expected)
    {
        using var response = await task;
        Assert.True(response.StatusCode == expected, $"Expected {expected}; got {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }
    private static async Task Sql(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync();
    }
    private static async Task<string?> Scalar(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand(); command.CommandText = sql; return (await command.ExecuteScalarAsync())?.ToString();
    }

    private sealed class BackupHost : WebApplicationFactory<Program>
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), $"vicikyu-db-backup-{Guid.NewGuid():N}");
        public string DbPath => Path.Combine(DirectoryPath, "live.db");
        public string Connection => $"Data Source={DbPath};Pooling=False";
        private readonly IDatabaseSnapshot? snapshot;
        private readonly bool observeGate;
        public ObservedMaintenance? ObservedGate { get; private set; }
        public PausingSheetSync PausedSync { get; } = new();
        private BackupHost(IDatabaseSnapshot? snapshot, bool observeGate)
        {
            this.snapshot = snapshot; this.observeGate = observeGate; System.IO.Directory.CreateDirectory(DirectoryPath);
        }
        public HttpClient Client() => CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing"); builder.UseSetting("ConnectionStrings:DefaultConnection", Connection);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ISheetClient>(); services.AddSingleton<ISheetClient, OfflineSheets>();
                services.RemoveAll<ISheetSyncService>(); services.AddSingleton<ISheetSyncService, FakeSheetSyncService>();
                if (snapshot is not null) { services.RemoveAll<IDatabaseSnapshot>(); services.AddSingleton(snapshot); }
                if (observeGate)
                {
                    ObservedGate = new(new DatabaseMaintenance(Connection));
                    services.RemoveAll<IDatabaseMaintenance>(); services.AddSingleton<IDatabaseMaintenance>(ObservedGate);
                    services.RemoveAll<ISheetSyncService>(); services.AddSingleton<ISheetSyncService>(PausedSync);
                }
            });
        }
        public static async Task<BackupHost> StartAsync(IDatabaseSnapshot? snapshot = null, bool observeGate = false)
        {
            var host = new BackupHost(snapshot, observeGate);
            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Database.MigrateAsync();
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            foreach (var role in new[] { "ADMIN", "MEMBER" }) Assert.True((await roles.CreateAsync(new IdentityRole(role))).Succeeded);
            foreach (var (name, role) in new[] { ("admin", "ADMIN"), ("member", "MEMBER") })
            {
                var user = new ApplicationUser { Id = name + "-id", UserName = name, StudentNumber = name, Name = "synthetic", Department = "test", Status = MemberStatus.Active };
                Assert.True((await users.CreateAsync(user, "pass")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
            }
            db.Settings.Add(new Setting { Key = "Google:ServiceAccountJson", Value = "synthetic-secret", UpdatedAt = DateTime.UtcNow });
            db.Activities.Add(new Activity { ActivityDate = new(2026, 1, 1), Status = ActivityStatus.Ready, CreatedByUserId = "admin-id", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
            return host;
        }
        public async Task Execute(string sql)
        {
            await using var connection = new SqliteConnection(Connection); await connection.OpenAsync(); await Sql(connection, sql);
        }
        public async Task<string?> Query(string sql)
        {
            await using var connection = new SqliteConnection(Connection); await connection.OpenAsync(); return await Scalar(connection, sql);
        }
        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            System.IO.Directory.Delete(DirectoryPath, recursive: true);
        }
        private sealed class OfflineSheets : ISheetClient
        {
            public Task<IReadOnlyList<SheetTab>> GetTabsAsync(string id, IEnumerable<string>? names = null, CancellationToken ct = default) => throw new InvalidOperationException("network forbidden");
            public Task WriteColumnAsync(string id, string tab, int index, SheetColumn column, CancellationToken ct = default) => throw new InvalidOperationException("network forbidden");
            public Task InsertColumnAsync(string id, string tab, int index, SheetColumn column, CancellationToken ct = default) => throw new InvalidOperationException("network forbidden");
            public Task DeleteColumnAsync(string id, string tab, int index, CancellationToken ct = default) => throw new InvalidOperationException("network forbidden");
        }
    }
}
