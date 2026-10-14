using VolleyballClub.Application.Abstractions;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Domain.Enums;
namespace VolleyballClub.Web.Api;
public static class BusinessEndpoints
{
    public static void MapBusinessApi(this WebApplication app)
    {
        var my = app.MapGroup("/api/my").RequireAuthorization().AddEndpointFilter<ApiValidation>();
        var admin = app.MapGroup("/api/admin").RequireAuthorization(policy => policy.RequireRole("ADMIN")).AddEndpointFilter<ApiValidation>();
        my.MapGet("/activity", async (IActivityService s, ICurrentUserService me) => { return Results.Json(await s.GetMyTodayAsync(await me.RequireUserIdAsync())); });
        admin.MapPost("/activity/today", async (IActivityService s) => { return Results.Json(await s.GetOrCreateTodayAsync()); });
        admin.MapGet("/activity/today", async (IActivityService s) => { return Results.Json(await s.GetTodayForAdminAsync()); });
        admin.MapPost("/activity/otp", async (IActivityService s) => { return Results.Json(await s.GenerateOtpAsync()); });
        admin.MapPost("/activity/start", async (IActivityService s) => { return Results.Json(await s.StartActivityAsync()); });
        admin.MapPost("/activity/new", async (IActivityService s) => { return Results.Json(await s.StartNewActivityAsync()); });
        admin.MapPost("/activity/cancel", async (IActivityService s) => { await s.CancelActivityAsync(); return Results.NoContent(); });
        admin.MapPost("/activity/close", async (IActivityService s) => { return Results.Json(await s.CloseActivityAsync()); });
        admin.MapPost("/activity/reopen", async (IActivityService s) => { return Results.Json(await s.ReopenActivityAsync()); });
        my.MapPost("/attendance", async (IAttendanceService s, TextRequest request) => { return Results.Json(await s.CheckInWithOtpAsync(request.Value)); });
        admin.MapGet("/attendance", async (IAttendanceService s, string? search) => { return Results.Json(await s.GetTodayMembersAsync(search)); });
        admin.MapPost("/attendance/{userId}", async (IAttendanceService s, string userId) => { await s.AdminAddAsync(userId); return Results.NoContent(); });
        admin.MapDelete("/attendance/{userId}", async (IAttendanceService s, string userId, [Microsoft.AspNetCore.Mvc.FromBody] AttendanceChangeRequest request) => { await s.AdminCancelAsync(userId, request.ConfirmTeamImpact); return Results.NoContent(); });
        admin.MapPost("/members/{userId}/attendance/{activityId}", async (IAttendanceService s, string userId, int activityId) => { await s.AdminAddForActivityAsync(userId, activityId); return Results.NoContent(); });
        admin.MapDelete("/members/{userId}/attendance/{activityId}", async (IAttendanceService s, string userId, int activityId, [Microsoft.AspNetCore.Mvc.FromBody] AttendanceChangeRequest request) => { await s.AdminRemoveForActivityAsync(userId, activityId, request.ConfirmTeamImpact); return Results.NoContent(); });
        admin.MapGet("/members/{userId}/attendance", async (IAttendanceService s, string userId) => { return Results.Json(await s.GetAdminRecordsAsync(userId)); });
        my.MapGet("/calendar", async (IAttendanceService s, int year, int month, ICurrentUserService me) => { return Results.Json(await s.GetMyCalendarAsync(await me.RequireUserIdAsync(), year, month)); });
        my.MapGet("/records", async (IAttendanceService s, int year, int month, ICurrentUserService me) => { return Results.Json(await s.GetMyRecordsAsync(await me.RequireUserIdAsync(), year, month)); });
        my.MapGet("/stats", async (IAttendanceService s, ICurrentUserService me) => { return Results.Json(await s.GetMySemesterStatsAsync(await me.RequireUserIdAsync())); });
        my.MapGet("/teams", async (ITeamService s) => { return Results.Json(await s.GetTodayTeamsAsync()); });
        admin.MapGet("/teams", async (ITeamService s) => { return Results.Json(await s.GetTodayTeamsForAdminAsync()); });
        admin.MapGet("/teams/participants", async (ITeamService s) => { return Results.Json(await s.GetParticipantsAsync()); });
        admin.MapPost("/teams/generate", async (ITeamService s, TeamCreateRequest request) => { return Results.Json(await s.GenerateAsync(request)); });
        admin.MapPut("/teams/members/{teamMemberId}", async (ITeamService s, int teamMemberId, TeamMoveRequest request) => { return Results.Json(await s.MoveMemberAsync(teamMemberId, request.TargetTeamId)); });
        admin.MapDelete("/teams/members/{teamMemberId}", async (ITeamService s, int teamMemberId) => { await s.RemoveMemberAsync(teamMemberId); return Results.NoContent(); });
        admin.MapPost("/teams/members", async (ITeamService s, TeamAddRequest request) => { return Results.Json(await s.AddMemberAsync(request.ParticipantKey, request.TeamId)); });
        admin.MapPost("/teams/cancel", async (ITeamService s) => { await s.CancelTeamsAsync(); return Results.NoContent(); });
        admin.MapPost("/teams/confirm", async (ITeamService s) => { await s.ConfirmAsync(); return Results.NoContent(); });
        admin.MapPost("/teams/guests", async (ITeamService s, TextRequest request) => { await s.AddGuestAsync(request.Value); return Results.NoContent(); });
        admin.MapDelete("/teams/guests/{guestId}", async (ITeamService s, int guestId) => { await s.RemoveGuestAsync(guestId); return Results.NoContent(); });
        admin.MapPost("/members/search", async (IMemberService s, MemberPageRequest request) => { return Results.Json(await s.GetPageAsync(request)); });
        admin.MapGet("/members/{userId}", async (IMemberService s, string userId) => { return Results.Json(await s.GetAsync(userId)); });
        admin.MapPost("/members", async (IMemberService s, MemberCreateRequest request) => { return Results.Json(await s.AddAsync(request)); });
        admin.MapPut("/members/{userId}", async (IMemberService s, string userId, MemberUpdateRequest request) => { return Results.Json(await s.UpdateAsync(userId, request)); });
        my.MapGet("/profile", async (IMemberService s, ICurrentUserService me) => { return Results.Json(await s.GetProfileAsync(await me.RequireUserIdAsync())); });
        my.MapPut("/profile/color", async (IMemberService s, TextRequest request) => { await s.UpdateMyColorAsync(request.Value); return Results.NoContent(); });
        my.MapPut("/profile/password", async (IMemberService s, PasswordChangeRequest request) => { await s.ChangeMyPasswordAsync(request); return Results.NoContent(); });
        admin.MapGet("/fees", async (IFeeService s) => { return Results.Json(await s.GetPeriodsAsync()); });
        my.MapGet("/fees/current", async (IFeeService s) =>
        {
            var period = await s.GetCurrentPeriodAsync();
            return period is null ? (IResult)Results.NoContent() : Results.Json(period);
        });
        my.MapGet("/fees", async (IFeeService s, ICurrentUserService me) => { return Results.Json(await s.GetMyStatusesAsync(await me.RequireUserIdAsync())); });
        admin.MapPost("/fees", async (IFeeService s, FeePeriodCreateRequest request) => { return Results.Json(await s.CreatePeriodAsync(request)); });
        admin.MapPut("/fees/{periodId}", async (IFeeService s, int periodId, FeePeriodUpdateRequest request) => { return Results.Json(await s.UpdatePeriodAsync(periodId, request)); });
        admin.MapDelete("/fees/{periodId}", async (IFeeService s, int periodId) => { await s.DeletePeriodAsync(periodId); return Results.NoContent(); });
        admin.MapGet("/fees/{periodId}/statuses", async (IFeeService s, int periodId, FeeFilter? filter, string? search) => { return Results.Json(await s.GetPeriodStatusesAsync(periodId, filter ?? FeeFilter.All, search)); });
        admin.MapPut("/fees/{periodId}/members/{userId}", async (IFeeService s, int periodId, string userId, FeePaymentRequest request) => { await s.SetPaidAsync(periodId, userId, request.IsPaid, request.Memo); return Results.NoContent(); });
        admin.MapGet("/semesters", async (ISemesterService s) => { return Results.Json(await s.GetSemestersAsync()); });
        admin.MapPost("/semesters", async (ISemesterService s, SemesterSaveRequest request) => { return Results.Json(await s.CreateAsync(request)); });
        admin.MapPut("/semesters/{semesterId}", async (ISemesterService s, int semesterId, SemesterSaveRequest request) => { return Results.Json(await s.UpdateAsync(semesterId, request)); });
        admin.MapDelete("/semesters/{semesterId}", async (ISemesterService s, int semesterId) => { await s.DeleteAsync(semesterId); return Results.NoContent(); });
        admin.MapGet("/sheets", async (ISheetAdminService s) => { return Results.Json(await s.GetListAsync()); });
        admin.MapPost("/sheets", async (ISheetAdminService s, SheetSaveRequest request) => { return Results.Json(await s.SaveAsync(request.Id, request.Name, request.SpreadsheetIdOrUrl)); });
        admin.MapDelete("/sheets/{id}", async (ISheetAdminService s, int id) => { await s.DeleteAsync(id); return Results.NoContent(); });
        admin.MapGet("/members/{userId}/mappings", async (ISheetMemberMappingService s, string userId) => { return Results.Json(await s.GetForMemberAsync(userId)); });
        admin.MapPut("/members/{userId}/mappings", async (ISheetMemberMappingService s, string userId, SheetMappingRequest request) =>
        {
            var report = await s.SetMappingAsync(request.SheetId, request.TabName, userId, request.RowLabel);
            return report is null ? (IResult)Results.NoContent() : Results.Json(report);
        });
        admin.MapGet("/audit", async (IAuditLogService s, int? take) => { return Results.Json(await s.GetRecentAsync(take ?? 100)); });
        admin.MapPut("/settings/google-key", async (TextRequest request, ISettingService s, IAuditLogService audit) =>
        {
            await s.SetAsync("Google:ServiceAccountJson", request.Value);
            await audit.LogAsync(AdminAction.SettingChange, "Setting", "Google:ServiceAccountJson", "구글 시트 키 변경");
            return Results.NoContent();
        });
        admin.MapPost("/sheets/resync", async (ISheetSyncService s, IAuditLogService audit) =>
        {
            var report = await s.ResyncAsync();
            await audit.LogAsync(AdminAction.SheetResync, "Sheet", "resync", $"활동 {report.ImportedActivities}건·출석 {report.ImportedAttendances}건 가져옴, 열 {report.ExportedColumns}개 내보냄");
            return Results.Json(report);
        });
    }
}
