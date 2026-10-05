using VolleyballClub.Application.Dtos;
using VolleyballClub.Domain.Enums;

namespace VolleyballClub.Application.Abstractions;

public interface IAttendanceService
{
    /// <summary>OTP로 당일 출석. 실패 사유는 AppException 계열로 전달한다.</summary>
    Task<DateTime> CheckInWithOtpAsync(string otp);

    /// <summary>관리자 직접 출석 추가.</summary>
    Task AdminAddAsync(string targetUserId);

    /// <summary>관리자 출석 취소. 팀이 이미 편성된 경우 confirmTeamImpact 필요.</summary>
    Task AdminCancelAsync(string targetUserId, bool confirmTeamImpact = false);

    /// <summary>관리자용: 전체 활성 부원 목록 + 당일 출석 여부(출석자 우선 정렬).</summary>
    Task<List<AttendanceMemberDto>> GetTodayMembersAsync(string? search = null);

    Task<MyCalendarDto> GetMyCalendarAsync(string userId, int year, int month);

    /// <summary>기록 페이지용: 해당 월의 모든 활동(최신순)에 내 출석·배정 팀·활동 상태를 묶어 반환한다.</summary>
    Task<MyRecordsDto> GetMyRecordsAsync(string userId, int year, int month);

    Task<MySemesterStatsDto> GetMySemesterStatsAsync(string userId);

    Task<List<AttendanceRankingDto>> GetRankingAsync(RankingPeriod period, string? viewerUserId = null);
}
