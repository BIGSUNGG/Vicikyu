# VolleyballClub — 대학교 배구부 출석·팀 편성 웹앱

대학 배구부의 **당일 운영**을 휴대폰에서 빠르게 처리하기 위한 모바일 우선 반응형 웹앱이다.
운영 당일의 핵심 흐름 — 출석 시작 → OTP 출석 → 회장 확인 → 랜덤 팀 편성 → 확정 → 전원 팀 확인 → 종료 —
를 최소한의 클릭으로 수행할 수 있도록 설계했다.

- OTP(4자리) 기반 당일 출석 + 관리자 직접 출석
- 당일 활동 상태머신: `READY → ATTENDANCE_OPEN → ATTENDANCE_CLOSED → TEAM_CREATED → ACTIVITY_CLOSED`
- 출석 인원 기반 랜덤 팀 편성(Fisher-Yates), 팀별 인원 지정, 팀원 이동, 확정
- 월간 캘린더 개인 출석 기록, 개최일 기준 출석률, 출석왕 랭킹(전체/이번 학기/이번 달, 공동 순위)
- 회비 기간·납부 관리, Google Sheets 사용 내역 링크
- ADMIN / MEMBER 역할 구분 — 화면 숨김과 별도로 **서비스 레이어에서 서버 권한 검증**
- 관리자 작업 AuditLog, 주요 파라미터는 DB Setting으로 관리

## 사용 기술

| 구분 | 기술 |
| --- | --- |
| 프레임워크 | ASP.NET Core Blazor Web App (**Interactive Server**, .NET 10) |
| UI | MudBlazor 9 (Snackbar 포함), 커스텀 모바일 앱 스타일 CSS |
| 데이터 | EF Core 10 + Npgsql / PostgreSQL 18 |
| 인증 | ASP.NET Core Identity (학번 + 비밀번호, Role 기반 Authorization) |
| 테스트 | xUnit (SQLite in-memory) |

## 솔루션 구조

```text
VolleyballClub.sln
├─ VolleyballClub.Web             # Blazor UI · Composition Root(Program.cs)
│  └─ Components/
│     ├─ Layout/  Pages/  Account/
│     ├─ Attendance/  Teams/  Ranking/  Fees/
├─ VolleyballClub.Application     # 공개 계약: 서비스 인터페이스 · DTO · 예외
├─ VolleyballClub.Domain          # 엔티티 · 열거형 · 학기/KST 규칙 (의존성 없음)
├─ VolleyballClub.Infrastructure  # EF Core DbContext · 서비스 구현 · Identity · Seed
└─ VolleyballClub.Tests           # xUnit 비즈니스 로직 테스트
```

의존 방향: `Web → Application + Infrastructure(조립)`, `Infrastructure → Application + Domain`,
`Application → Domain`, `Domain → (없음)`.

## 실행 방법

### 필요 사항

- .NET SDK 10 (`dotnet --list-sdks` 로 확인)
- PostgreSQL 14+ (로컬 서비스 실행 중)

### 1) DB 준비

데이터베이스를 생성한다(이미 있으면 생략):

```bash
psql -h localhost -U postgres -c "CREATE DATABASE volleyballclub;"
```

### 2) 연결 문자열 설정 (user-secrets — 소스코드에 커밋되지 않음)

```bash
cd VolleyballClub.Web
dotnet user-secrets init   # 처음 1회 (템플릿이 이미 지정했다면 생략)
dotnet user-secrets set "ConnectionStrings:DefaultConnection" \
  "Host=localhost;Port=5432;Database=volleyballclub;Username=postgres;Password=<비밀번호>"
```

> ConnectionString은 appsettings.json에 넣지 않는다. 시크릿은 user-secrets/환경변수만 사용한다.

### 3) 마이그레이션 적용

```bash
dotnet ef database update --project VolleyballClub.Infrastructure --startup-project VolleyballClub.Web
```

새 마이그레이션을 만들어야 할 때:

```bash
dotnet ef migrations add <이름> --project VolleyballClub.Infrastructure --startup-project VolleyballClub.Web --output-dir Persistence/Migrations
```

개발 환경에서 `dotnet run` 시에도 마이그레이션이 자동 적용된다(`DbInitializer`).

### 4) 실행

```bash
dotnet run --project VolleyballClub.Web
# http://localhost:5121
```

휴대폰에서 접속하려면(같은 Wi-Fi):

```bash
dotnet run --project VolleyballClub.Web --urls http://0.0.0.0:5121
# 방화벽 인바운드 규칙에서 5121 포트 허용 후 휴대폰 브라우저에서 http://<PC아이피>:5121 접속
```

## Seed 데이터 (개발 환경 자동)

Development 환경 첫 기동 시 자동 생성(멱등 — 사용자가 이미 있으면 건너뜀):

| 항목 | 값 |
| --- | --- |
| 관리자 | 학번 `admin` / 비밀번호 `Admin1234!` (김회장, ADMIN) |
| 일반 부원 15명 | 학번 `202512001`~`202512015` / 비밀번호 `Member1234!` |
| 오늘 활동 | AttendanceOpen 상태 + OTP + 출석 12명 샘플 |
| 회비 | 현재 학기 기간 1건, 납부 11 / 미납 4 |
| 설정 | SchoolName/ClubName/FeeSheetUrl/DefaultTeamCount/OtpLength |

> 운영 배포 전에 시드 비밀번호를 반드시 변경하라.

## 주요 기능 · 권한 구조

### MEMBER

로그인 → OTP 출석 → 오늘 팀 확인 → 출석왕 순위 → MY(프로필·월간 캘린더·출석률) → 회비 납부 여부/사용 내역 링크

### ADMIN (부원 기능 +)

오늘 활동 생성·OTP 생성/변경·출석 시작/마감·활동 마감/재오픈, 직접 출석 추가/취소, 게스트 추가,
팀 개수·팀별 인원 지정 → 랜덤 편성 → 다시 뽑기 → 팀원 이동 → 확정, 부원 추가/수정/비활성화(soft delete),
회비 기간·납부 관리, 사이트 설정, 감사 로그 조회

모든 관리자 기능은 `AuthorizeView`/`[Authorize(Roles="ADMIN")]` 로 화면을 숨기는 동시에
**서비스 생성자 경로의 `RequireAdminAsync()`가 서버에서 재차 검증**한다. OTP 값은 ADMIN 전용 DTO에만 존재한다.

## 보안 요약

- 비밀번호: Identity PasswordHasher (평문 저장 없음)
- 중복 출석 차단: 서버 선검사 + `Attendances(ActivityId, UserId)` UNIQUE 인덱스 최종 방어
- OTP: 암호학적으로 안전한 난수(`RandomNumberGenerator`), 상수 시간 비교, MEMBER 비노출
- 비활성화 부원: 로그인 차단 + 보안 스탬프 갱신으로 세션 무효화
- SQL Injection: EF Core LINQ 파라미터화 (문자열 조합 SQL 없음)
- 관리자 주요 작업: AuditLog 기록

## 테스트 / 빌드

```bash
dotnet build
dotnet test
```

커버 영역: OTP 출석(정상/오류/중복), 마감 후 출석 거부, 팀 인원 합계 검증, Fisher-Yates 배정(전원 1회 배정 불변식),
다시 뽑기/팀원 이동/확정 상태 전이, 랭킹 집계(공동 순위·Ready 제외), 회비 변경 권한 차단, MEMBER의 관리자 서비스 호출 거부,
그리고 §50 하루 전체 흐름의 통합 시나리오 테스트.

## 배포 방식 개요

1. 서버에 .NET 10 Runtime(ASP.NET Core 포함) + PostgreSQL 설치
2. `dotnet publish VolleyballClub.Web -c Release -o ./publish`
3. ConnectionString을 환경변수로 주입: `ConnectionStrings__DefaultConnection=...`
4. `ASPNETCORE_ENVIRONMENT=Production` 으로 실행(프로덕션에선 시드 자동 실행 없음 — 초기 관리자는 직접 생성)
5. 리버스 프록시(Nginx/IIS) 뒤에 두고 HTTPS 종단 처리 권장

향후 확장 여지: 소셜 로그인(Kakao/Google/Microsoft — 인증 구성이 Web/Account에 분리되어 있음),
CSV/Excel 부원 명단·출석 기록 내보내기, Google Sheets 동기화, 알림.
