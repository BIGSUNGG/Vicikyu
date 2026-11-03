using VolleyballClub.Application.Dtos;

namespace VolleyballClub.Application.Abstractions;

/// <summary>활동 기록 xlsx 가져오기·내보내기(관리자 설정 페이지).
/// 업로드 파일은 스트리밍으로 파싱하고 서버 디스크에 저장하지 않는다.</summary>
public interface IActivityArchiveService
{
    /// <summary>학기별 탭 xlsx를 읽어 활동·출석·예정/없음 표시를 DB에 병합한다(멱등 — 재업로드 시 중복 없음).</summary>
    Task<ActivityImportReport> ImportAsync(Stream stream, CancellationToken ct = default);

    /// <summary>전체 활동 기록을 학기별 탭 xlsx(단일 파일)로 내보낸다.</summary>
    Task<byte[]> ExportAsync(CancellationToken ct = default);
}
