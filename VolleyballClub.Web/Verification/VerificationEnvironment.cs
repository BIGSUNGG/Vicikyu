using VolleyballClub.Application.Abstractions;
using VolleyballClub.Application.Dtos;

namespace VolleyballClub.Web.Verification;

/// <summary>Explicitly isolated Docker regression environment; never enabled by Development/Production.</summary>
internal static class VerificationEnvironment
{
    internal const string Name = "Verification";
    internal const string Connection = "Data Source=/tmp/vicikyu-wasm-audit.db";

    internal static void Configure(WebApplicationBuilder builder)
    {
        if (builder.Configuration.GetConnectionString("DefaultConnection") != Connection)
            throw new InvalidOperationException("Verification requires its isolated /tmp database.");
        if (!string.IsNullOrEmpty(builder.Configuration["Google:ServiceAccountJson"]) ||
            !string.IsNullOrEmpty(builder.Configuration["Google:SpreadsheetId"]))
            throw new InvalidOperationException("Verification must not receive Google credentials or live sheet IDs.");
        if (ActivityManagementVerification.Enabled(builder.Environment, builder.Configuration))
        {
            builder.Services.AddSingleton<TimeProvider, ActivityManagementVerification.FixtureClock>();
            builder.Services.AddSingleton<MutableActivitySheets>();
            builder.Services.AddSingleton<ISheetClient>(sp => sp.GetRequiredService<MutableActivitySheets>());
            // Keep the real scoped SheetSyncService registered by AddInfrastructure.
        }
        else
        {
            builder.Services.AddSingleton<ISheetClient>(new FixtureSheets());
            builder.Services.AddSingleton<ISheetSyncService>(new FixtureSync());
        }
    }

    private sealed class FixtureSheets : ISheetClient
    {
        public Task<IReadOnlyList<SheetTab>> GetTabsAsync(string id, IEnumerable<string>? names = null, CancellationToken ct = default)
        {
            IReadOnlyList<SheetTab> tabs = [new("2026-2", [new string[] { "이름" }, new string[] { "김민수" }])];
            return Task.FromResult<IReadOnlyList<SheetTab>>(names is null ? tabs : tabs.Where(t => names.Contains(t.Name)).ToArray());
        }
        public Task WriteColumnAsync(string id, string tab, int index, SheetColumn column, CancellationToken ct = default) => Task.CompletedTask;
        public Task InsertColumnAsync(string id, string tab, int index, SheetColumn column, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteColumnAsync(string id, string tab, int index, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FixtureSync : ISheetSyncService
    {
        public SheetPushResult? LastPushResult => null;
        public Task<bool> ExportClosedActivityAsync(int id, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> ExportScheduledActivityAsync(int id, CancellationToken ct = default) => Task.FromResult(true);
        public Task<SheetSyncReport> SyncAsync(CancellationToken ct = default) => Task.FromResult(new SheetSyncReport(0, 0, 0, 0, []));
        public Task<SheetSyncReport> ResyncAsync(CancellationToken ct = default) => SyncAsync(ct);
        public Task<SheetPushResult> PushActivityAsync(int id, CancellationToken ct = default) => Task.FromResult(new SheetPushResult(true, true, null));
        public Task<bool> RemoveCancelledActivityAsync(int id, CancellationToken ct = default) => Task.FromResult(true);
        public Task<SheetSyncStatusDto> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(new SheetSyncStatusDto(0, []));
    }
}
