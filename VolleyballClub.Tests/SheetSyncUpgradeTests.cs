using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using VolleyballClub.Domain.Entities;
using VolleyballClub.Domain.Enums;
using VolleyballClub.Infrastructure.Identity;
using VolleyballClub.Infrastructure.Persistence;

namespace VolleyballClub.Tests;

public sealed class SheetSyncUpgradeTests
{
    [Fact]
    public async Task Upgrade_marks_old_live_sessions_and_removed_mapped_attendees_pending()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vicikyu-sheet-upgrade-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
            await using var db = new ApplicationDbContext(options);
            var previous = db.Database.GetMigrations().TakeWhile(m => !m.EndsWith("_PendingSheetWrites")).Last();
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync(previous);
            db.Users.Add(new ApplicationUser { Id = "a", StudentNumber = "A", Name = "A", Status = MemberStatus.Active });
            var sheet = new SheetDefinition { Name = "synthetic", SpreadsheetId = "synthetic", Kind = SheetKind.Attendance };
            db.SheetDefinitions.Add(sheet);
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO Activities (Id, ActivityDate, Status, CreatedByUserId, CreatedAt, UpdatedAt)
                VALUES (5, '2026-10-01', 1, 'a', '2026-10-01', '2026-10-01');
                """);
            db.ActivitySources.Add(new ActivitySource { Id = "old-source", SheetDefinitionId = sheet.Id,
                TabName = "2026-2", Date = new(2026, 10, 1), Kind = SheetDayKind.Scheduled, ActivityId = 5 });
            db.SheetMemberMappings.Add(new SheetMemberMapping { SheetDefinitionId = sheet.Id, TabName = "2026-2", UserId = "a", RowLabel = "A" });
            await db.SaveChangesAsync();
            // No attendance remains in the old DB: this user may have been cancelled after the last live export.
            Assert.False(await db.Attendances.AnyAsync());
            await migrator.MigrateAsync();
            db.ChangeTracker.Clear();
            var activity = await db.Activities.SingleAsync();
            Assert.NotNull(activity.SheetSyncPendingAt);
            Assert.Equal(1, activity.SheetSyncRevision);
            Assert.Equal(new[] { "a" }, activity.SheetSyncRequiredUserIds);
            Assert.Equal(ActivityStatus.AttendanceOpen, activity.Status);
            Assert.False(await db.Attendances.AnyAsync());
        }
        finally
        {
            foreach (var file in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(file)) File.Delete(file);
        }
    }
}
