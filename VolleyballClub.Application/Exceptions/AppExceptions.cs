namespace VolleyballClub.Application.Exceptions;

/// <summary>사용자에게 그대로 보여줄 수 있는 메시지를 담는 애플리케이션 예외 기본형.</summary>
public class AppException(string message) : Exception(message)
{
}

/// <summary>요청한 리소스를 찾을 수 없음(예: 오늘 Activity 없음).</summary>
public class NotFoundException(string message) : AppException(message)
{
}

/// <summary>권한 없음 — 일반 부원의 관리자 기능 호출 차단 등.</summary>
public class ForbiddenException(string message = "관리자 권한이 필요합니다.") : AppException(message)
{
}

/// <summary>비즈니스 규칙 위반(검증 실패).</summary>
public class RuleViolationException(string message) : AppException(message)
{
}

/// <summary>상태 충돌 — 이미 출석됨, 마감 후 수정, 중복 등.</summary>
public class ConflictException(string message) : AppException(message)
{
}
