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

    /// <summary>관리자: 지정 활동(과거·마감 포함)에 직접 출석 추가 — 부원 상세 정정용.</summary>
    Task AdminAddForActivityAsync(string targetUserId, int activityId);

    /// <summary>관리자: 지정 활동의 출석 기록 삭제(해당 활동 팀에서도 제외). 팀 편성이 있으면 confirmTeamImpact 필요.</summary>
    Task AdminRemoveForActivityAsync(string targetUserId, int activityId, bool confirmTeamImpact = false);

    /// <summary>관리자: 부원 하나의 출석 기록(최신순) + 이번 달/학기/총 집계 + 출석 추가 가능 활동 목록.</summary>
    Task<MemberAttendanceAdminDto> GetAdminRecordsAsync(string userId);

    /// <summary>관리자용: 전체 활성 부원 목록 + 당일 출석 여부(출석자 우선 정렬).</summary>
    Task<List<AttendanceMemberDto>> GetTodayMembersAsync(string? search = null);

    Task<MyCalendarDto> GetMyCalendarAsync(string userId, int year, int month);

    /// <summary>기록 페이지용: 해당 월의 모든 활동(최신순)에 내 출석·배정 팀·활동 상태를 묶어 반환한다.</summary>
    Task<MyRecordsDto> GetMyRecordsAsync(string userId, int year, int month);

    Task<MySemesterStatsDto> GetMySemesterStatsAsync(string userId);

    Task<List<AttendanceRankingDto>> GetRankingAsync(RankingPeriod period, string? viewerUserId = null);
}
