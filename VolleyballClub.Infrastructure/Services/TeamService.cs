using Microsoft.EntityFrameworkCore;
using VolleyballClub.Application.Abstractions;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Application.Exceptions;
using VolleyballClub.Domain;
using VolleyballClub.Domain.Entities;
using VolleyballClub.Domain.Enums;
using VolleyballClub.Infrastructure.Persistence;

namespace VolleyballClub.Infrastructure.Services;

public class TeamService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ICurrentUserService currentUser,
    IAuditLogService auditLog,
    TimeProvider timeProvider) : ITeamService
{
    private const string TeamNamePrefix = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    public async Task<List<TeamResultDto>> GetTodayTeamsAsync()
    {
        var today = KstClock.Today(timeProvider);
        await using var db = await dbFactory.CreateDbContextAsync();
        var activity = await db.Activities.AsNoTracking().FirstOrDefaultAsync(a => a.ActivityDate == today);
        if (activity is null || activity.Status != ActivityStatus.TeamCreated || activity.ConfirmedDrawNumber is not { } confirmed)
        {
            return [];
        }

        return await LoadTeamsAsync(db, activity.Id, confirmed);
    }

    public async Task<List<TeamResultDto>> GetTodayTeamsForAdminAsync()
    {
        await currentUser.RequireAdminAsync();
        var today = KstClock.Today(timeProvider);
        await using var db = await dbFactory.CreateDbContextAsync();
        var activity = await db.Activities.AsNoTracking().FirstOrDefaultAsync(a => a.ActivityDate == today);
        if (activity is null)
        {
            return [];
        }

        var latest = await LatestDrawNumberAsync(db, activity.Id);
        return latest is null ? [] : await LoadTeamsAsync(db, activity.Id, latest);
    }

    public async Task<int?> GetConfirmedDrawNumberAsync()
    {
        await currentUser.RequireAdminAsync();
        var today = KstClock.Today(timeProvider);
        await using var db = await dbFactory.CreateDbContextAsync();
        var activity = await db.Activities.AsNoTracking().FirstOrDefaultAsync(a => a.ActivityDate == today);
        return activity?.ConfirmedDrawNumber;
    }

    /// <summary>해당 활동의 최신 회차 번호. 팀이 없으면 null.</summary>
    private static async Task<int?> LatestDrawNumberAsync(ApplicationDbContext db, int activityId) =>
        await db.Teams.AsNoTracking()
            .Where(t => t.ActivityId == activityId)
            .OrderByDescending(t => t.DrawNumber)
            .Select(t => (int?)t.DrawNumber)
            .FirstOrDefaultAsync();

    public async Task<ParticipantListDto> GetParticipantsAsync()
    {
        await currentUser.RequireAdminAsync();
        var today = KstClock.Today(timeProvider);
        await using var db = await dbFactory.CreateDbContextAsync();
        var activity = await ActivityService.FindTodayAsync(db, today);

        var attendedUsers = await db.Attendances.AsNoTracking()
            .Where(a => a.ActivityId == activity.Id)
            .Join(db.Users.AsNoTracking(),
                a => a.UserId,
                u => u.Id,
                (a, u) => new { a.UserId, u.Name, u.Department })
            .ToListAsync();

        var guests = await db.Guests.AsNoTracking()
            .Where(g => g.ActivityId == activity.Id)
            .Select(g => new { g.Id, g.Name })
            .ToListAsync();

        var participants = new List<ParticipantDto>();
        foreach (var u in attendedUsers)
        {
            participants.Add(new ParticipantDto($"u:{u.UserId}", u.UserId, null, u.Name, u.Department, false, true));
        }

        foreach (var g in guests)
        {
            participants.Add(new ParticipantDto($"g:{g.Id}", null, g.Id, g.Name, "게스트", true, true));
        }

        return new ParticipantListDto(participants, attendedUsers.Count);
    }

    public async Task<List<TeamResultDto>> GenerateAsync(TeamCreateRequest request)
    {
        await currentUser.RequireAdminAsync();

        var sizes = request.TeamSizes;
        if (sizes.Count < 2 || sizes.Count > 10)
        {
            throw new RuleViolationException("팀 수는 2 이상이어야 합니다.");
        }

        if (sizes.Any(s => s < 1))
        {
            throw new RuleViolationException("팀별 인원수는 1 이상이어야 합니다.");
        }

        var userIds = request.UserIds.Distinct().ToList();
        var guestIds = request.GuestIds.Distinct().ToList();
        var participantCount = userIds.Count + guestIds.Count;
        var sizeSum = sizes.Sum();
        if (sizeSum != participantCount)
        {
            throw new RuleViolationException($"팀별 인원수 합계가 참가 인원 {participantCount}명과 일치하지 않습니다.");
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        var activity = await ActivityService.FindTodayAsync(db, KstClock.Today(timeProvider));
        if (!activity.CanDrawTeams)
        {
            throw new ConflictException(activity.Status == ActivityStatus.ActivityClosed
                ? "현재 활동이 종료되어 팀을 편성할 수 없습니다."
                : "출석을 마감한 후 팀을 편성할 수 있습니다.");
        }

        var validUsers = await db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id) && u.IsActive)
            .ToDictionaryAsync(u => u.Id, u => u.Name);
        foreach (var userId in userIds)
        {
            if (!validUsers.ContainsKey(userId))
            {
                throw new RuleViolationException("참가자 목록에 유효하지 않은 부원이 포함되어 있습니다.");
            }
        }

        var validGuests = await db.Guests.AsNoTracking()
            .Where(g => guestIds.Contains(g.Id) && g.ActivityId == activity.Id)
            .ToDictionaryAsync(g => g.Id, g => g.Name);
        foreach (var guestId in guestIds)
        {
            if (!validGuests.ContainsKey(guestId))
            {
                throw new RuleViolationException("존재하지 않는 게스트가 포함되어 있습니다.");
            }
        }

        // 다시 뽑기: 기존 회차를 지우지 않고 다음 회차로 생성한다(회차 이력 보존)
        var drawNumber = await LatestDrawNumberAsync(db, activity.Id) ?? 0;
        drawNumber++;

        var now = KstClock.NowUtc(timeProvider);
        var members = new List<(string? UserId, int? GuestId, string Name)>();
        members.AddRange(userIds.Select(id => ((string?)id, (int?)null, validUsers[id])));
        members.AddRange(guestIds.Select(id => ((string?)null, (int?)id, validGuests[id])));

        Shuffle(members);

        var cursor = 0;
        for (var i = 0; i < sizes.Count; i++)
        {
            var team = new Team
            {
                ActivityId = activity.Id,
                Name = $"{TeamNamePrefix[i % 26]} TEAM",
                Order = i,
                DrawNumber = drawNumber,
                CreatedAt = now,
            };
            db.Teams.Add(team);

            foreach (var member in members.Skip(cursor).Take(sizes[i]))
            {
                db.TeamMembers.Add(new TeamMember
                {
                    Team = team,
                    UserId = member.UserId,
                    GuestId = member.GuestId,
                    CreatedAt = now,
                });
            }

            cursor += sizes[i];
        }

        activity.UpdatedAt = now;
        await db.SaveChangesAsync();
        await auditLog.LogAsync(AdminAction.TeamCreate, "Activity", activity.Id.ToString(),
            $"{drawNumber}회차 {sizes.Count}팀 편성 (참가 {participantCount}명)");

        return await LoadTeamsAsync(db, activity.Id, drawNumber);
    }

    public async Task<TeamResultDto> MoveMemberAsync(int teamMemberId, int targetTeamId)
    {
        await currentUser.RequireAdminAsync();
        await using var db = await dbFactory.CreateDbContextAsync();

        var member = await db.TeamMembers
            .Include(m => m.Team)
            .FirstOrDefaultAsync(m => m.Id == teamMemberId)
            ?? throw new NotFoundException("팀원을 찾을 수 없습니다.");

        var targetTeam = await db.Teams.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == targetTeamId && t.ActivityId == member.Team!.ActivityId)
            ?? throw new NotFoundException("이동 대상 팀을 찾을 수 없습니다.");

        if (member.TeamId == targetTeam.Id)
        {
            throw new RuleViolationException("이미 해당 팀 소속입니다.");
        }

        var today = KstClock.Today(timeProvider);
        var activity = await db.Activities.AsNoTracking().FirstAsync(a => a.Id == member.Team!.ActivityId);
        if (activity.ActivityDate != today || activity.Status == ActivityStatus.ActivityClosed)
        {
            throw new ConflictException("현재 활동이 종료되어 수정할 수 없습니다.");
        }

        var latestDraw = await LatestDrawNumberAsync(db, activity.Id);
        if (member.Team!.DrawNumber != latestDraw || targetTeam.DrawNumber != latestDraw)
        {
            throw new RuleViolationException("이전 회차 팀은 수정할 수 없습니다. 최신 회차에서 이동해 주세요.");
        }

        var memberName = await ResolveMemberNameAsync(db, member);
        var fromTeamName = member.Team!.Name;
        member.TeamId = targetTeam.Id;
        member.Team = null!;
        await db.SaveChangesAsync();
        await auditLog.LogAsync(AdminAction.TeamChange, "TeamMember", member.Id.ToString(),
            $"{memberName}: {fromTeamName} → {targetTeam.Name}");

        var teams = await LoadTeamsAsync(db, activity.Id);
        return teams.First(t => t.TeamId == targetTeam.Id);
    }

    public async Task ConfirmAsync()
    {
        await currentUser.RequireAdminAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var activity = await ActivityService.FindTodayAsync(db, KstClock.Today(timeProvider));

        var latestDraw = await LatestDrawNumberAsync(db, activity.Id)
            ?? throw new RuleViolationException("먼저 팀을 편성해 주세요.");

        if (activity.ConfirmedDrawNumber == latestDraw)
        {
            return; // 이미 최신 회차가 확정됨 — 멱등
        }

        if (!activity.CanConfirmTeams)
        {
            throw new ConflictException("출석을 마감한 후 팀을 확정할 수 있습니다.");
        }

        activity.Status = ActivityStatus.TeamCreated;
        activity.ConfirmedDrawNumber = latestDraw;
        activity.UpdatedAt = KstClock.NowUtc(timeProvider);
        await db.SaveChangesAsync();
        await auditLog.LogAsync(AdminAction.TeamConfirm, "Activity", activity.Id.ToString(), $"{latestDraw}회차 팀 편성 확정");
    }

    public async Task AddGuestAsync(string name)
    {
        await currentUser.RequireAdminAsync();
        var trimmed = name.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new RuleViolationException("게스트 이름을 입력해 주세요.");
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        var activity = await ActivityService.FindTodayAsync(db, KstClock.Today(timeProvider));
        if (activity.Status == ActivityStatus.ActivityClosed)
        {
            throw new ConflictException("현재 활동이 종료되어 수정할 수 없습니다.");
        }

        var now = KstClock.NowUtc(timeProvider);
        db.Guests.Add(new Guest { ActivityId = activity.Id, Name = trimmed, CreatedAt = now });
        await db.SaveChangesAsync();
        await auditLog.LogAsync(AdminAction.GuestAdd, "Activity", activity.Id.ToString(), $"게스트 '{trimmed}' 추가");
    }

    public async Task RemoveGuestAsync(int guestId)
    {
        await currentUser.RequireAdminAsync();
        await using var db = await dbFactory.CreateDbContextAsync();
        var guest = await db.Guests.Include(g => g.Activity).FirstOrDefaultAsync(g => g.Id == guestId)
            ?? throw new NotFoundException("게스트를 찾을 수 없습니다.");

        if (guest.Activity.ActivityDate != KstClock.Today(timeProvider) || guest.Activity.Status == ActivityStatus.ActivityClosed)
        {
            throw new ConflictException("현재 활동이 종료되어 수정할 수 없습니다.");
        }

        var guestTeamMembers = await db.TeamMembers.Where(m => m.GuestId == guestId).ToListAsync();
        db.TeamMembers.RemoveRange(guestTeamMembers);
        db.Guests.Remove(guest);
        await db.SaveChangesAsync();
        await auditLog.LogAsync(AdminAction.GuestRemove, "Activity", guest.ActivityId.ToString(), $"게스트 '{guest.Name}' 제거");
    }

    private static async Task<string> ResolveMemberNameAsync(ApplicationDbContext db, TeamMember member) =>
        member.UserId is { } userId
            ? await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Name).FirstOrDefaultAsync() ?? "알 수 없음"
            : member.GuestId is { } guestId
                ? await db.Guests.AsNoTracking().Where(g => g.Id == guestId).Select(g => g.Name).FirstOrDefaultAsync() ?? "게스트"
                : "알 수 없음";

    private static async Task<List<TeamResultDto>> LoadTeamsAsync(ApplicationDbContext db, int activityId, int? drawNumber = null)
    {
        var teams = await db.Teams.AsNoTracking()
            .Where(t => t.ActivityId == activityId && (drawNumber == null || t.DrawNumber == drawNumber))
            .OrderBy(t => t.Order)
            .Select(t => new { t.Id, t.Name, t.Order, t.DrawNumber })
            .ToListAsync();

        var teamMembers = await db.TeamMembers.AsNoTracking()
            .Where(m => m.Team!.ActivityId == activityId)
            .Select(m => new { m.Id, m.TeamId, m.UserId, m.GuestId })
            .ToListAsync();

        var users = await db.Users.AsNoTracking().ToListAsync();
        var guests = await db.Guests.AsNoTracking().Where(g => g.ActivityId == activityId).ToListAsync();
        var userNames = users.ToDictionary(u => u.Id, u => u.Name);
        var guestNames = guests.ToDictionary(g => g.Id, g => g.Name);

        return teams.Select(t => new TeamResultDto(
                t.Id,
                t.Name,
                t.Order,
                t.DrawNumber,
                teamMembers
                    .Where(m => m.TeamId == t.Id)
                    .Select(m => new TeamMemberDto(
                        m.Id,
                        m.UserId,
                        m.GuestId,
                        m.UserId is { } uid ? userNames.GetValueOrDefault(uid, "알 수 없음")
                            : m.GuestId is { } gid ? guestNames.GetValueOrDefault(gid, "게스트")
                            : "알 수 없음",
                        m.GuestId is not null))
                    .ToList()))
            .ToList();
    }

    /// <summary>Fisher-Yates shuffle — Guid.OrderBy 방식이 아닌 명확한 셔플 구현.</summary>
    private static void Shuffle<T>(IList<T> list)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
