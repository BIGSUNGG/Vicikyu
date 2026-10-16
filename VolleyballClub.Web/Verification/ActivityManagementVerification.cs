using Microsoft.EntityFrameworkCore;
using VolleyballClub.Application.Abstractions;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Domain.Entities;
using VolleyballClub.Domain.Enums;
using VolleyballClub.Infrastructure.Persistence;

namespace VolleyballClub.Web.Verification;

internal static class ActivityManagementVerification
{
    internal static bool Enabled(IHostEnvironment environment, IConfiguration configuration) =>
        environment.IsEnvironment(VerificationEnvironment.Name) && configuration.GetValue<bool>("Verification:ActivityManagement");

    internal sealed class FixtureClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);
    }

    internal static async Task SeedAsync(WebApplication app)
    {
        if (!Enabled(app.Environment, app.Configuration)) return;
        using var scope = app.Services.CreateScope();
        var sp = scope.ServiceProvider;
        await using var db = await sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContextAsync();
        if (db.Database.GetConnectionString() != VerificationEnvironment.Connection || await db.SheetDefinitions.AnyAsync()
            || await db.Settings.AnyAsync(s => s.Key.StartsWith("Google:")))
            throw new InvalidOperationException("Activity verification requires an isolated seeded database without sheets or Google settings.");
        var users = await db.Users.OrderBy(u => u.StudentNumber).ToListAsync();
        if (users.Count != 16 || users.Count(u => u.StudentNumber == "admin") != 1 || users.Count(u => u.StudentNumber.StartsWith("2025120")) != 15)
            throw new InvalidOperationException("Activity verification requires only the standard synthetic seed members.");
        var sheets = sp.GetRequiredService<MutableActivitySheets>();
        var first = new SheetDefinition { Name = "합성 출석부 A", SpreadsheetId = MutableActivitySheets.First, Kind = SheetKind.Attendance };
        var second = new SheetDefinition { Name = "합성 출석부 B", SpreadsheetId = MutableActivitySheets.Second, Kind = SheetKind.Attendance };
        db.SheetDefinitions.AddRange(first, second);
        await db.SaveChangesAsync();
        var members = users.Where(u => u.StudentNumber != "admin").ToList();
        var currentAttendees = await db.Attendances.Select(a => a.UserId).ToListAsync();
        List<IReadOnlyList<string>> current = [
            new[] { "활동 상태", "활동 완료", "활동 예정", "활동 예정", "활동 없음(시험)" },
            new[] { "부원", "26. 10. 01. 목", "26. 10. 16. 금", "26. 10. 20. 화", "26. 10. 22. 목" }];
        foreach (var user in members) current.Add(new[] { user.Name, "", currentAttendees.Contains(user.Id) ? "출석" : "", "", "" });
        current.Add(new[] { "총 참여자 수", "=COUNTIF(B3:B17,\"출석\")", "=COUNTIF(C3:C17,\"출석\")", "", "" });
        sheets.Seed(first.SpreadsheetId, new("2026-2", current));
        sheets.Seed(first.SpreadsheetId, new("2026-1", new IReadOnlyList<string>[] { new[] { "부원", "26. 03. 01. 일" }, new[] { members[0].Name, "1" }, new[] { "총 참여자 수", "=SUM(B2:B2)" } }));
        sheets.Seed(second.SpreadsheetId, new("2026-2", new IReadOnlyList<string>[] {
            new[] { "활동 상태", "활동 완료" }, new[] { "부원", "26. 10. 02. 금" }, new[] { members[0].Name, "출석" }, new[] { "총 참여자 수", "=COUNTIF(B3:B3,\"출석\")" } }));
        foreach (var user in members)
            db.SheetMemberMappings.Add(new() { SheetDefinitionId = first.Id, TabName = "2026-2", UserId = user.Id, RowLabel = user.Name });
        foreach (var (sheet, tab) in new[] { (first, "2026-1"), (second, "2026-2") })
            db.SheetMemberMappings.Add(new() { SheetDefinitionId = sheet.Id, TabName = tab, UserId = members[0].Id, RowLabel = members[0].Name });
        await db.SaveChangesAsync();
        await sp.GetRequiredService<ISheetSyncService>().SyncAsync();
        db.ChangeTracker.Clear();
        var liveSource = await db.ActivitySources.SingleAsync(s => s.SheetDefinitionId == first.Id && s.Date == new DateOnly(2026, 10, 16));
        liveSource.ActivityId = (await db.Activities.SingleAsync(a => a.ActivityDate == new DateOnly(2026, 10, 16))).Id;
        var completed = await db.ActivitySources.SingleAsync(s => s.SheetDefinitionId == first.Id && s.Date == new DateOnly(2026, 10, 1));
        var guest = new Guest { ActivityId = completed.ActivityId!.Value, Name = "합성 게스트" };
        db.Guests.Add(guest);
        await db.SaveChangesAsync();
        db.Teams.Add(new Team { ActivityId = completed.ActivityId.Value, Name = "합성 팀", Members = [new TeamMember { GuestId = guest.Id }] });
        await db.SaveChangesAsync();
    }

    internal static void MapEndpoints(WebApplication app)
    {
        if (!Enabled(app.Environment, app.Configuration)) return;
        var group = app.MapGroup("/api/verification/activity-management")
            .RequireAuthorization(policy => policy.RequireRole("ADMIN"));
        group.MapPost("/fault", (FaultRequest request, MutableActivitySheets sheets) => { sheets.Arm(request.Mode); return Results.NoContent(); });
        // Read-only evidence: never invokes a mutation service or repairs state on the verifier's behalf.
        group.MapGet("/state", async (IDbContextFactory<ApplicationDbContext> factory, MutableActivitySheets sheets) =>
        {
            await using var db = await factory.CreateDbContextAsync();
            return Results.Json(new
            {
                fakeGoogle = true, liveGoogle = false, clock = "2026-10-16T09:00:00Z", database = db.Database.GetConnectionString(), source = sheets.State(),
                members = await db.Users.OrderBy(u => u.StudentNumber).Select(u => new { u.Id, u.StudentNumber, u.Name }).ToListAsync(),
                sheets = await db.SheetDefinitions.OrderBy(s => s.Id).Select(s => new { s.Id, s.Name, s.SpreadsheetId }).ToListAsync(),
                mappings = await db.SheetMemberMappings.OrderBy(m => m.Id).Select(m => new { m.Id, m.SheetDefinitionId, m.TabName, m.UserId, m.RowLabel }).ToListAsync(),
                sources = await db.ActivitySources.OrderBy(s => s.Id).Select(s => new { s.Id, s.SheetDefinitionId, s.TabName, s.Date, s.Kind, s.Reason, s.ActivityId, s.IsMissing }).ToListAsync(),
                activities = await db.Activities.OrderBy(a => a.Id).Select(a => new { a.Id, a.ActivityDate, a.Status }).ToListAsync(),
                attendances = await db.Attendances.OrderBy(a => a.Id).Select(a => new { a.Id, a.ActivityId, a.UserId }).ToListAsync(),
                guests = await db.Guests.OrderBy(g => g.Id).Select(g => new { g.Id, g.ActivityId }).ToListAsync(),
                teams = await db.Teams.OrderBy(t => t.Id).Select(t => new { t.Id, t.ActivityId }).ToListAsync(),
                teamMembers = await db.TeamMembers.OrderBy(t => t.Id).Select(t => new { t.Id, t.TeamId, t.UserId, t.GuestId }).ToListAsync(),
                semesters = await db.SemesterPeriods.OrderBy(s => s.Id).Select(s => new { s.Id, s.Name }).ToListAsync(),
                auditIds = await db.AuditLogs.OrderBy(a => a.Id).Select(a => a.Id).ToListAsync(),
                auditCount = await db.AuditLogs.CountAsync(), journalCount = await db.SheetEditJournals.CountAsync()
            });
        });
    }
    internal sealed record FaultRequest(string Mode);
}
