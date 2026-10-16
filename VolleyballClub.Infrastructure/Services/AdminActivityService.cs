using Microsoft.EntityFrameworkCore;
using VolleyballClub.Application.Abstractions;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Application.Exceptions;
using VolleyballClub.Domain;
using VolleyballClub.Domain.Entities;
using VolleyballClub.Domain.Enums;
using VolleyballClub.Infrastructure.Persistence;

namespace VolleyballClub.Infrastructure.Services;

public sealed class AdminActivityService(IDbContextFactory<ApplicationDbContext> factory,
    ISheetClient client, ISheetFormatResolver resolver, ICurrentUserService currentUser,
    ISheetSyncService sync, TimeProvider clock) : IAdminActivityService
{
    public async Task<IReadOnlyList<ActivityTargetDto>> GetTargetsAsync(CancellationToken ct = default)
    {
        await currentUser.RequireAdminAsync();
        await using var db = await factory.CreateDbContextAsync(ct);
        var result = new List<ActivityTargetDto>();
        foreach (var sheet in await db.SheetDefinitions.Where(s => s.Kind == SheetKind.Attendance).ToListAsync(ct))
            foreach (var tab in await client.GetTabsAsync(sheet.SpreadsheetId, ct: ct))
                if (resolver.ResolveByTabName(tab.Name) is { } f)
                    result.Add(new(sheet.Id, sheet.Name, tab.Name, f.From, f.To));
        return result;
    }

    public async Task<IReadOnlyList<ManagedActivityDto>> GetActivitiesAsync(CancellationToken ct = default)
    {
        await currentUser.RequireAdminAsync();
        await sync.SyncAsync(ct);
        await ManagedSheetEdits.Gate.WaitAsync(ct);
        try
        {
            await ManagedSheetEdits.RecoverAsync(factory, client);
            await using var db = await factory.CreateDbContextAsync(ct);
            return await ListAsync(db, ct);
        }
        finally { ManagedSheetEdits.Gate.Release(); }
    }

    private static bool Editable(Activity? activity) => activity is not null && activity.Status != ActivityStatus.Ready;
    private static string State(ActivitySource? source, Activity? activity) => activity?.Status switch
    {
        ActivityStatus.AttendanceOpen => "활동 중", ActivityStatus.TeamCreated => "팀 확정",
        ActivityStatus.AttendanceClosed => "출석 마감", ActivityStatus.Ready => "준비 중",
        ActivityStatus.ActivityClosed => "활동 완료",
        _ => source?.Kind == SheetDayKind.NoActivity
            ? "활동 없음" + (string.IsNullOrWhiteSpace(source.Reason) ? "" : $"({source.Reason})")
            : "활동 예정"
    };
    private static ManagedActivityDto Dto(ActivitySource? s, Activity? a, string label) => new(
        a is not null && a.CreatedByUserId != Activity.SheetImportCreator ? $"app:{a.Id}" : s?.Id ?? $"app:{a!.Id}", a?.Id, s?.SheetDefinitionId, s?.TabName, s?.Date ?? a!.ActivityDate,
        s?.IsMissing == true ? label + " (원본 삭제)" : label, s?.Kind ?? (a!.Status == ActivityStatus.ActivityClosed ? SheetDayKind.Completed : SheetDayKind.Scheduled),
        s?.Reason, a?.Status, State(s, a), Editable(a) && s?.IsMissing != true, a?.Attendances.Count ?? 0);
    private static async Task<List<ManagedActivityDto>> ListAsync(ApplicationDbContext db, CancellationToken ct)
    {
        var sheets = await db.SheetDefinitions.ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var sources = await db.ActivitySources.Include(s => s.Activity).ThenInclude(a => a!.Attendances).ToListAsync(ct);
        var bound = sources.Where(s => s.ActivityId != null).Select(s => s.ActivityId!.Value).ToList();
        var apps = await db.Activities.Include(a => a.Attendances).Where(a => !bound.Contains(a.Id)).ToListAsync(ct);
        return sources.Select(s => Dto(s, s.Activity, $"{sheets[s.SheetDefinitionId]} / {s.TabName}"))
            .Concat(apps.Select(a => Dto(null, a, "앱"))).OrderByDescending(a => a.Date).ThenBy(a => a.Key).ToList();
    }

    private async Task<T> Locked<T>(Func<ApplicationDbContext, Task<T>> action, CancellationToken ct)
    {
        await currentUser.RequireAdminAsync();
        await ManagedSheetEdits.Gate.WaitAsync(ct);
        try
        {
            await ManagedSheetEdits.RecoverAsync(factory, client);
            await using var db = await factory.CreateDbContextAsync(ct);
            return await action(db);
        }
        finally { ManagedSheetEdits.Gate.Release(); }
    }
    private static void Validate(SheetDayKind kind, string? reason)
    {
        if (!Enum.IsDefined(kind)) throw new RuleViolationException("활동 상태가 올바르지 않습니다.");
        if (reason?.Length > 500) throw new RuleViolationException("사유는 500자 이하여야 합니다.");
    }
    private async Task<(SheetDefinition Sheet, ISemesterSheetFormat Format, ManagedSheetTab Tab)> Target(
        ApplicationDbContext db, int sheetId, string tabName, DateOnly date)
    {
        var sheet = await db.SheetDefinitions.SingleOrDefaultAsync(s => s.Id == sheetId && s.Kind == SheetKind.Attendance)
            ?? throw new NotFoundException("출석 시트를 찾을 수 없습니다.");
        var format = resolver.ResolveByTabName(tabName) ?? throw new RuleViolationException("지원하지 않는 탭입니다.");
        if (!format.CoversDate(date)) throw new RuleViolationException("선택한 학기의 날짜가 아닙니다.");
        return (sheet, format, await client.GetManagedTabAsync(sheet.SpreadsheetId, tabName));
    }
    private static List<string> Column(ManagedSheetTab tab, int index) => tab.RawValues
        .Select(r => r.Count >= index ? r[index - 1] : "{}").ToList();
    private static string Header(DateOnly date) => $"{date:yy}. {date:MM}. {date:dd}. {new[] { "일", "월", "화", "수", "목", "금", "토" }[(int)date.DayOfWeek]}";
    private static bool NewFormat(ManagedSheetTab tab) => tab.Rows.Count > 0 && tab.Rows[0].Any(v =>
        v.Trim() is "활동 상태" or "활동 예정" or "활동 완료" || v.Trim().StartsWith("활동 없음"));
    private static void Set(List<string> values, int row, string text)
    {
        while (values.Count <= row) values.Add("{}");
        values[row] = ManagedSheetEdits.Raw(text);
    }
    private static void SetHeader(List<string> values, ManagedSheetTab tab, DateOnly date, SheetDayKind kind, string? reason)
    {
        var state = kind switch { SheetDayKind.Completed => "활동 완료", SheetDayKind.Scheduled => "활동 예정", _ => "활동 없음" + (string.IsNullOrWhiteSpace(reason) ? "" : $"({reason.Trim()})") };
        if (NewFormat(tab)) { Set(values, 0, state); Set(values, 1, Header(date)); }
        else Set(values, 0, Header(date) + " " + state);
    }
    private static List<string> NewColumn(ManagedSheetTab tab, ISemesterSheetFormat format, DateOnly date, SheetDayKind kind, string? reason)
    {
        var built = format.BuildColumn(new SheetTab(tab.Name, tab.Rows), date, new HashSet<string>());
        var cells = Enumerable.Repeat("{}", tab.Rows.Count).ToList();
        foreach (var part in new[] { built.Column, built.StatusColumn }.OfType<SheetColumn>())
            for (var i = 0; i < part.Cells.Count; i++) Set(cells, part.FirstRowIndex - 1 + i, part.Cells[i]);
        SetHeader(cells, tab, date, kind, reason);
        return cells;
    }
    private static int AppendIndex(ManagedSheetTab tab) => Math.Max(2, tab.RawValues.Select(r => r.Count).DefaultIfEmpty(1).Max() + 1);
    private static ManagedSheetMutation Mutation(SheetMutationKind kind, int column, string key, IReadOnlyList<string> cells) =>
        new(Guid.NewGuid().ToString("N"), kind, column, key, cells);

    public Task<ManagedActivityDto> CreateAsync(ActivityCreateRequest request, CancellationToken ct = default) => Locked(async db =>
    {
        Validate(request.Kind, request.Reason);
        if (request.Kind == SheetDayKind.Completed) throw new RuleViolationException("새 날짜는 예정 또는 활동 없음으로 만드세요.");
        var (sheet, format, tab) = await Target(db, request.SheetId, request.TabName, request.Date);
        if (format.FindDateColumnIndex(new SheetTab(tab.Name, tab.Rows), request.Date) is not null)
        {
            var existing = await db.ActivitySources.FirstOrDefaultAsync(s => s.SheetDefinitionId == sheet.Id && s.TabName == tab.Name && s.Date == request.Date);
            if (existing is not null && existing.Kind == request.Kind && existing.Reason == CleanReason(request.Kind, request.Reason))
                return (await ListAsync(db, ct)).Single(s => s.Key == existing.Id); // retry after a committed response was lost
            throw new ConflictException("선택한 탭에 같은 날짜가 이미 있습니다.");
        }
        var source = new ActivitySource { SheetDefinitionId = sheet.Id, TabName = tab.Name, Date = request.Date,
            Kind = request.Kind, Reason = CleanReason(request.Kind, request.Reason) };
        var index = AppendIndex(tab);
        var cells = NewColumn(tab, format, source.Date, source.Kind, source.Reason);
        // The totals formula returned by the format used its insertion index; adjust only this new column's formula.
        for (var i = 0; i < cells.Count; i++)
            if (ManagedSheetEdits.Text(cells[i]).StartsWith('='))
                cells[i] = ManagedSheetEdits.Raw(TotalsFormula(tab, index));
        await ManagedSheetEdits.CommitAsync(db, client, sheet.SpreadsheetId, tab,
            Mutation(SheetMutationKind.Insert, index, source.Id, cells), async () =>
            { db.ActivitySources.Add(source); await AuditAsync(db, source, AdminAction.ActivityCreate, "생성"); });
        return (await ListAsync(db, ct)).Single(s => s.Key == source.Id);
    }, ct);

    private async Task AuditAsync(ApplicationDbContext db, ActivitySource source, AdminAction action, string verb)
    {
        db.AuditLogs.Add(new AuditLog { AdminUserId = await currentUser.RequireUserIdAsync(), Action = action,
            TargetType = "ActivitySource", TargetId = source.Id,
            Details = $"{source.TabName} {source.Date:yyyy-MM-dd} 활동 {verb}", CreatedAt = KstClock.NowUtc(clock) });
    }

    private static string TotalsFormula(ManagedSheetTab tab, int index)
    {
        var letters = "";
        for (var n = index; n > 0; n = (n - 1) / 26) letters = (char)('A' + (n - 1) % 26) + letters;
        var start = NewFormat(tab) ? 3 : 2;
        var totalRow = tab.Rows.ToList().FindIndex(r => r.Count > 0 && r[0].Trim() == "총 참여자 수");
        return NewFormat(tab) ? $"=COUNTIF({letters}{start}:{letters}{totalRow},\"출석\")" : $"=SUM({letters}{start}:{letters}{totalRow})";
    }
    private static string? CleanReason(SheetDayKind kind, string? reason) => kind == SheetDayKind.NoActivity && !string.IsNullOrWhiteSpace(reason) ? reason.Trim() : null;
    internal static async Task RebuildMarks(ApplicationDbContext db)
    {
        var sources = await db.ActivitySources.Where(s => s.Kind != SheetDayKind.Completed && s.ActivityId == null && !s.IsMissing).ToListAsync();
        db.SheetDayMarks.RemoveRange(await db.SheetDayMarks.ToListAsync());
        await db.SaveChangesAsync(CancellationToken.None);
        db.SheetDayMarks.AddRange(sources.GroupBy(s => s.Date).Select(g => new SheetDayMark { Date = g.Key, Kind = g.Any(s => s.Kind == SheetDayKind.Scheduled) ? SheetDayKind.Scheduled : SheetDayKind.NoActivity }));
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private async Task<ActivitySource> Source(ApplicationDbContext db, string key, int? sheetId = null, string? tabName = null)
    {
        var source = await db.ActivitySources.Include(s => s.Activity).ThenInclude(a => a!.Attendances).SingleOrDefaultAsync(s => s.Id == key);
        if (source is not null) return source;
        if (!key.StartsWith("app:") || !int.TryParse(key[4..], out var id)) throw new NotFoundException("활동을 찾을 수 없습니다.");
        var activity = await db.Activities.Include(a => a.Attendances).SingleOrDefaultAsync(a => a.Id == id)
            ?? throw new NotFoundException("활동을 찾을 수 없습니다.");
        source = await db.ActivitySources.Include(s => s.Activity).ThenInclude(a => a!.Attendances).SingleOrDefaultAsync(s => s.ActivityId == id);
        if (source is not null) return source;
        // An unbound app session can only be routed when the target is unambiguous. Never pick the first sheet.
        var targets = (await GetTargetsAsync()).Where(t => activity.ActivityDate >= t.From && activity.ActivityDate <= t.To).ToList();
        if (sheetId is not null || tabName is not null)
            targets = targets.Where(t => t.SheetId == sheetId && t.TabName == tabName).ToList();
        if (targets.Count != 1) throw new ConflictException("앱 활동의 대상 시트/탭을 유일하게 정할 수 없습니다.");
        var t = targets.Single();
        var (sheet, format, tab) = await Target(db, t.SheetId, t.TabName, activity.ActivityDate);
        source = new ActivitySource { ActivityId = id, Activity = activity, SheetDefinitionId = sheet.Id, TabName = tab.Name,
            Date = activity.ActivityDate, Kind = activity.Status == ActivityStatus.ActivityClosed ? SheetDayKind.Completed : SheetDayKind.Scheduled };
        return source;
    }

    private async Task<(int Index, bool Insert, List<string> Cells)> SourceCells(ApplicationDbContext db,
        ActivitySource source, ManagedSheetTab tab, ISemesterSheetFormat format)
    {
        var columns = tab.ColumnKeys.Where(k => k.Value == source.Id).Select(k => k.Key).ToList();
        if (columns.Count == 1) return (columns[0], false, Column(tab, columns[0]));
        if (db.Entry(source).State != EntityState.Detached || columns.Count > 1)
            throw new ConflictException("활동 원본 열이 없거나 중복입니다. 시트를 갱신하세요.");
        var cells = NewColumn(tab, format, source.Date, source.Kind, source.Reason);
        var mappings = await db.SheetMemberMappings.Where(m => m.SheetDefinitionId == source.SheetDefinitionId && m.TabName == tab.Name).ToListAsync();
        var attending = source.Activity?.Attendances.Select(a => a.UserId).ToHashSet() ?? [];
        for (var row = NewFormat(tab) ? 2 : 1; row < tab.Rows.Count; row++)
        {
            var label = tab.Rows[row].FirstOrDefault()?.Trim();
            if (label is "총 참여자 수" or "탈퇴/퇴출자") break;
            var mapping = mappings.SingleOrDefault(m => m.RowLabel == label);
            if (mapping is not null && attending.Contains(mapping.UserId)) Set(cells, row, NewFormat(tab) ? "출석" : "1");
        }
        var index = AppendIndex(tab);
        for (var i = 0; i < cells.Count; i++)
            if (ManagedSheetEdits.Text(cells[i]).StartsWith('=')) cells[i] = ManagedSheetEdits.Raw(TotalsFormula(tab, index));
        return (index, true, cells);
    }

    public Task<ManagedActivityDto> EditAsync(string key, ActivityEditRequest request, CancellationToken ct = default) => Locked(async db =>
    {
        var s = await Source(db, key, request.SheetId, request.TabName);
        var kind = request.Kind ?? s.Kind;
        Validate(kind, request.Reason);
        if (kind == SheetDayKind.NoActivity && s.Activity?.Attendances.Count > 0)
            throw new ConflictException("출석자가 있는 활동은 활동 없음으로 바꿀 수 없습니다.");
        if (request.Kind == SheetDayKind.Scheduled && s.Activity?.Attendances.Count > 0)
            throw new ConflictException("출석자가 있는 활동은 예정으로 되돌릴 수 없습니다.");
        if (request.Kind == SheetDayKind.Completed && s.Activity is { Status: ActivityStatus.Ready })
            throw new ConflictException("시작하지 않은 앱 활동은 운영 화면에서 시작하세요.");
        var (sheet, format, tab) = await Target(db, s.SheetDefinitionId, s.TabName, request.Date);
        var (index, insert, cells) = await SourceCells(db, s, tab, format);
        if (request.Kind is SheetDayKind.NoActivity or SheetDayKind.Scheduled
            && format.Parse(new SheetTab(tab.Name, tab.Rows)).Activities.Any(c => c.ColumnIndex == index && c.AttendedRowLabels.Count > 0))
            throw new ConflictException("원본 시트에 출석자가 있는 활동은 없음/예정으로 바꿀 수 없습니다.");
        var duplicate = format.Parse(new SheetTab(tab.Name, tab.Rows)).Activities.Any(c => c.Date == request.Date && c.ColumnIndex != index);
        if (request.Date != s.Date && duplicate) throw new ConflictException("선택한 탭에 같은 날짜가 이미 있습니다.");
        var reason = CleanReason(kind, request.Reason);
        SetHeader(cells, tab, request.Date, kind, reason);
        await ManagedSheetEdits.CommitAsync(db, client, sheet.SpreadsheetId, tab, Mutation(insert ? SheetMutationKind.Insert : SheetMutationKind.Write, index, s.Id, cells), async () =>
        {
            // Requery persisted attendance inside the commit transaction, after remote I/O and under the writer gate.
            if (request.Kind is SheetDayKind.NoActivity or SheetDayKind.Scheduled && s.ActivityId is { } activityId
                && await db.Attendances.AnyAsync(a => a.ActivityId == activityId))
                throw new ConflictException("출석자가 있는 활동은 없음/예정으로 바꿀 수 없습니다.");
            if (db.Entry(s).State == EntityState.Detached) db.ActivitySources.Add(s);
            s.Date = request.Date; s.Kind = kind; s.Reason = reason;
            if (s.Activity is not null)
            {
                s.Activity.ActivityDate = request.Date;
                if (kind != SheetDayKind.Completed && request.Kind is not null)
                {
                    db.Activities.Remove(s.Activity); s.Activity = null; s.ActivityId = null;
                }
            }
            if (kind == SheetDayKind.Completed && s.Activity is null) s.Activity = Completed(s.Date);
            else if (request.Kind == SheetDayKind.Completed && s.Activity is { } closing && closing.Status != ActivityStatus.ActivityClosed)
            {
                closing.Status = ActivityStatus.ActivityClosed;
                closing.ActivityClosedAt = closing.AttendanceClosedAt = KstClock.NowUtc(clock);
                closing.SheetExportedAt = closing.UpdatedAt = KstClock.NowUtc(clock);
                db.Teams.RemoveRange(db.Teams.Where(t => t.ActivityId == closing.Id));
            }
            await AuditAsync(db, s, AdminAction.SettingChange, "수정");
        });
        return (await ListAsync(db, ct)).Single(r => r.Key == s.Id || r.ActivityId == s.ActivityId && s.ActivityId != null);
    }, ct);

    private Activity Completed(DateOnly date)
    {
        var now = KstClock.NowUtc(clock);
        return new Activity { ActivityDate = date, Status = ActivityStatus.ActivityClosed, CreatedByUserId = Activity.SheetImportCreator,
            CreatedAt = now, UpdatedAt = now, AttendanceOpenedAt = now, AttendanceClosedAt = now, ActivityClosedAt = now, SheetExportedAt = now };
    }
    internal static int FindSourceColumn(ManagedSheetTab tab, string key)
    {
        var columns = tab.ColumnKeys.Where(k => k.Value == key).Select(k => k.Key).ToList();
        return columns.Count == 1 ? columns[0] : throw new ConflictException("활동 원본 열이 없거나 중복입니다. 시트를 갱신하세요.");
    }
    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        await Locked(async db =>
        {
            if (key.StartsWith("app:") && int.TryParse(key[4..], out var id))
            {
                var app = await db.Activities.SingleOrDefaultAsync(a => a.Id == id, ct);
                if (app is null) return true; // Retry of an already committed app deletion.
                if (!await db.ActivitySources.AnyAsync(s => s.ActivityId == id, ct))
                {
                    if (app.Status != ActivityStatus.Ready || app.AttendanceOpenedAt is not null || app.SheetExportedAt is not null)
                        throw new ConflictException("앱 활동의 원본 연결을 확인한 후 삭제하세요.");
                    db.AuditLogs.Add(new AuditLog { AdminUserId = await currentUser.RequireUserIdAsync(),
                        Action = AdminAction.ActivityCancel, TargetType = "Activity", TargetId = id.ToString(),
                        Details = $"{app.ActivityDate:yyyy-MM-dd} 활동 삭제", CreatedAt = KstClock.NowUtc(clock) });
                    db.Activities.Remove(app);
                    await db.SaveChangesAsync(ct);
                    return true;
                }
            }
            else if (!key.StartsWith("app:") && !await db.ActivitySources.AnyAsync(s => s.Id == key)) return true;
            var s = await Source(db, key);
            var (sheet, _, tab) = await Target(db, s.SheetDefinitionId, s.TabName, s.Date);
            await ManagedSheetEdits.CommitAsync(db, client, sheet.SpreadsheetId, tab,
                Mutation(SheetMutationKind.Delete, FindSourceColumn(tab, s.Id), s.Id, []), async () =>
                {
                    await AuditAsync(db, s, AdminAction.ActivityCancel, "삭제");
                    if (s.Activity is not null) db.Activities.Remove(s.Activity);
                    db.ActivitySources.Remove(s);
                });
                return true;
        }, ct);
    }

    public Task<IReadOnlyList<AttendanceMemberDto>> GetMembersAsync(string key, CancellationToken ct = default) => Locked<IReadOnlyList<AttendanceMemberDto>>(async db =>
    {
        var rows = await ListAsync(db, ct);
        var row = rows.SingleOrDefault(r => r.Key == key) ?? throw new NotFoundException("활동을 찾을 수 없습니다.");
        var attendance = await db.Attendances.Where(a => a.ActivityId == row.ActivityId).ToDictionaryAsync(a => a.UserId, ct);
        var users = await db.Users.OrderBy(u => u.Name).ToListAsync(ct);
        return users.Select(u => { var a = attendance.GetValueOrDefault(u.Id); return new AttendanceMemberDto(u.Id, u.Name, u.StudentNumber,
            u.Department, a is not null, a?.CheckedAt, a?.AttendanceType, a?.Id ?? 0); }).ToList();
    }, ct);

    public async Task SetAttendanceAsync(string key, string userId, ActivityAttendanceRequest request, CancellationToken ct = default)
    {
        await Locked(async db =>
        {
            var s = await Source(db, key);
            var a = s.Activity;
            if (!Editable(a) || s.Kind == SheetDayKind.NoActivity) throw new ConflictException("시작 또는 완료된 활동만 출석을 수정할 수 있습니다.");
            var member = await db.Users.SingleOrDefaultAsync(u => u.Id == userId) ?? throw new NotFoundException("부원을 찾을 수 없습니다.");
            var existing = a!.Attendances.SingleOrDefault(x => x.UserId == userId);
            var teams = await db.TeamMembers.Where(m => m.UserId == userId && m.Team!.ActivityId == a.Id).ToListAsync();
            if (!request.IsAttended && teams.Count > 0 && !request.ConfirmTeamImpact)
                throw new ConflictException("팀 편성이 완료되어 있습니다. 출석을 취소하면 팀에서도 제외됩니다. 계속하시겠습니까?");
            var (sheet, format, tab) = await Target(db, s.SheetDefinitionId, s.TabName, s.Date);
            var (index, insert, cells) = await SourceCells(db, s, tab, format);
            var mappings = await db.SheetMemberMappings.Where(m => m.SheetDefinitionId == sheet.Id && m.TabName == tab.Name).ToListAsync();
            var representedLabels = tab.Rows.Skip(NewFormat(tab) ? 2 : 1)
                .TakeWhile(r => r.FirstOrDefault()?.Trim() is not ("총 참여자 수" or "탈퇴/퇴출자"))
                .Select(r => r.FirstOrDefault()?.Trim()).ToHashSet();
            if (!mappings.Any(m => m.UserId == userId && representedLabels.Contains(m.RowLabel)))
                throw new ConflictException("부원의 시트 행 매핑이 필요합니다. 매핑 후 다시 시도하세요.");
            var beforeCells = cells.ToList();
            // The fresh source owns everyone else's marks, even when the DB cache is stale.
            var start = NewFormat(tab) ? 2 : 1;
            for (var row = start; row < tab.Rows.Count; row++)
            {
                var label = tab.Rows[row].FirstOrDefault()?.Trim();
                if (label is "총 참여자 수" or "탈퇴/퇴출자") break;
                var mapping = mappings.SingleOrDefault(m => m.RowLabel == label);
                if (mapping?.UserId == userId) Set(cells, row, request.IsAttended ? NewFormat(tab) ? "출석" : "1" : NewFormat(tab) && a.Status == ActivityStatus.ActivityClosed ? "미출석" : "");
            }
            var attendanceChanged = (existing is not null) != request.IsAttended;
            if (!insert && !attendanceChanged && cells.SequenceEqual(beforeCells)) return true;
            await ManagedSheetEdits.CommitAsync(db, client, sheet.SpreadsheetId, tab, Mutation(insert ? SheetMutationKind.Insert : SheetMutationKind.Write, index, s.Id, cells), async () =>
            {
                if (db.Entry(s).State == EntityState.Detached) db.ActivitySources.Add(s);
                if (!attendanceChanged) return;
                var now = KstClock.NowUtc(clock);
                if (request.IsAttended) a.Attendances.Add(new Attendance { UserId = userId, AttendanceType = AttendanceType.Admin, CheckedAt = now, CreatedAt = now });
                else { db.Attendances.Remove(existing!); db.TeamMembers.RemoveRange(teams); }
                db.AuditLogs.Add(new AuditLog { AdminUserId = await currentUser.RequireUserIdAsync(),
                    Action = request.IsAttended ? AdminAction.AttendanceAdd : AdminAction.AttendanceRemove, TargetType = "User", TargetId = userId,
                    Details = $"{member.Name} {s.Date:yyyy-MM-dd} 출석 {(request.IsAttended ? "추가" : "취소")}", CreatedAt = now });
            });
            return true;
        }, ct);
    }

    public async Task<MemberActivityListDto> GetMemberActivitiesAsync(string userId, CancellationToken ct = default)
    {
        await currentUser.RequireAdminAsync();
        var activities = await GetActivitiesAsync(ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        if (!await db.Users.AnyAsync(u => u.Id == userId, ct)) throw new NotFoundException("부원을 찾을 수 없습니다.");
        var attendance = await db.Attendances.Where(a => a.UserId == userId).Select(a => new { a.ActivityId, a.Activity!.ActivityDate }).ToListAsync(ct);
        var teams = await db.TeamMembers.Where(m => m.UserId == userId).Select(m => m.Team!.ActivityId).ToListAsync(ct);
        var today = KstClock.Today(clock);
        var (from, to) = await SemesterPeriodResolver.GetCurrentRangeAsync(db, today);
        return new(userId, attendance.Count(a => a.ActivityDate.Year == today.Year && a.ActivityDate.Month == today.Month),
            attendance.Count(a => a.ActivityDate >= from && a.ActivityDate <= to), attendance.Count,
            activities.Select(a => new MemberActivityRowDto(a.Key, a.ActivityId, a.Date, a.SourceLabel, a.StateText,
                attendance.Any(x => x.ActivityId == a.ActivityId), a.CanEditAttendance, a.ActivityId is { } id && teams.Contains(id))).ToList());
    }
}
