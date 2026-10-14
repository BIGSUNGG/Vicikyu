# Blazor WebAssembly 전환

[인테이크] 명확화 0건 · [부정검토] 이견 없음

## 범위와 상태

.NET 10을 유지하면서 로그인·로그아웃 포함 전체 UI를 WASM으로 전환했다. ASP.NET 호스트·Identity·SQLite·Google Sheets는 같은 출처의 서버에 유지한다. 운영 배포·운영 DB/볼륨 변경은 수행하지 않는다. 구현·자동 검증과 별개로 **독립 리뷰가 아직 미완료**이므로 목표 완료를 선언하지 않는다. 리뷰 상태는 [blazor-wasm-review.md](blazor-wasm-review.md)에 기록한다.

## 전환 전후 경로·기능 대응

전환 전의 경로는 모두 `VolleyballClub.Web/Components/` 아래, 전환 후에는 모두 `VolleyballClub.Client/Components/` 아래의 같은 상대 경로로 이동했다. 전에는 업무 화면이 Interactive Server, 계정 화면이 정적 SSR이었으며, 전환 후에는 모두 Interactive WebAssembly다. 기존 17개 경로를 보존한다.

| 경로 | 권한 | 전후 동일한 Components 상대 경로 | 보존 기능 / 서버 경계 |
| --- | --- | --- | --- |
| `/` | 로그인 | `Pages/Home.razor` | 오늘 활동·OTP 출석·학기 통계·회비·확정 팀·확대 보기; `/api/my/*` |
| `/my` | 로그인 | `Pages/MyPage.razor` | 프로필·월간 달력·통계·색·비밀번호 변경; `/api/my/profile`, `/calendar`, `/stats` |
| `/records` | 로그인 | `Pages/Records.razor` | 월간 출석 기록·통계; `/api/my/records`, `/stats` |
| `/admin` | ADMIN | `Pages/Admin/Dashboard.razor` | 활동 생성·시작·마감·재오픈·새 활동·취소·OTP·시트 갱신; `/api/admin/activity/*`, `/sheets/resync` |
| `/admin/attendance` | ADMIN | `Pages/Admin/Attendance.razor` | 검색·직접 출석 추가/취소·팀 영향 확인; `/api/admin/attendance/*` |
| `/admin/teams` | ADMIN | `Pages/Admin/Teams.razor` | 인원 선택·게스트·인원 수·랜덤 편성·재편성·이동/제거/추가·확정; `/api/admin/teams/*` |
| `/admin/members` | ADMIN | `Pages/Admin/Members.razor` | 검색·필터·정렬·페이지·부원 추가; `/api/admin/members/search`, `/members` |
| `/admin/members/{UserId}` | ADMIN | `Pages/Admin/MemberDetail.razor` | 정보/역할/상태/색/비밀번호·과거 출석·시트 매핑; `/api/admin/members/{userId}/*` |
| `/admin/fees` | ADMIN | `Pages/Admin/Fees.razor` | 기간 CRUD·검색·필터·납부; `/api/admin/fees/*` |
| `/admin/semesters` | ADMIN | `Pages/Admin/Semesters.razor` | 학기 CRUD; `/api/admin/semesters/*` |
| `/admin/settings` | ADMIN | `Pages/Admin/Settings.razor` | 시트 등록/수정/삭제·자격증명 쓰기만; `/api/admin/sheets/*`, `/settings/google-key` |
| `/admin/audit` | ADMIN | `Pages/Admin/AuditLogs.razor` | 감사 기록 읽기; `/api/admin/audit` |
| `/Account/Login` | 공개 | `Account/Pages/Login.razor` | 학번·비밀번호·로컬 복귀 경로; `/api/auth/login` |
| `/Account/Logout` | 공개 | `Account/Pages/Logout.razor` | 명시적 로그아웃 확인; `/api/auth/logout` |
| `/Account/AccessDenied` | 공개 | `Account/Pages/AccessDenied.razor` | 로그인된 사용자의 권한 부족 안내 |
| `/Error` | 공개 | `Pages/Error.razor` | 안전한 오류 안내; 서버 HttpContext 의존 제거 |
| `/not-found` | 공개 | `Pages/NotFound.razor` | 경로 없음 안내 |

인증되지 않은 사용자는 보호 화면에서 로그인으로, 역할이 부족한 사용자는 AccessDenied로 이동한다. UI 셸의 HTTP 200은 권한 승인 증거가 아니며 API가 독립적으로 401/403을 반환한다.

## 의존·인증·데이터 경계

- `Client → Application → Domain`; 브라우저에서 쓰는 메서드만 가진 도메인별 구체 HTTP 클라이언트를 등록한다. Infrastructure·EF Core·Identity 저장소·Google 클라이언트 참조나 임의 RPC는 없다.
- `Web → Client + Application + Infrastructure(조립)`; 기존 업무 서비스와 규칙은 서버에서 재사용한다. `CurrentUserService`는 circuit 대신 인증된 HTTP 주체를 읽는다.
- `Web/Components/App.razor`만 HTML 호스트로 남는다. Routes/HeadOutlet은 `InteractiveWebAssemblyRenderMode(prerender: false)`이며 `blazor.web.js`·기존 정적 자산·manifest 경로를 유지한다. Server 렌더 모드·circuit·reconnect UI를 제거했다.
- `/api/my/*`의 대상 회원 ID는 서버의 인증 주체에서 결정한다. 브라우저가 다른 ID를 제출해 개인 데이터를 고를 수 없다. `/api/admin/*`에는 ADMIN 권한이 필요하다.
- 로그인/로그아웃 포함 모든 unsafe API 요청은 쿠키에 바인딩된 `X-CSRF-TOKEN` 검증을 거친다. 인증 주체가 바뀌면 브라우저 토큰·인증 상태를 갱신한다. 매 요청에서 활성 상태·보안 스탬프·현재 역할을 확인하며 쿠키/인증 비밀은 서버에 남는다.
- 자신의 비밀번호 변경은 Identity의 이전 스탬프 무효화를 유지한다. 브라우저 인증 상태도 즉시 갱신해 재로그인 화면으로 이동한다. 관리자 초기화·상태/역할 변경도 이전 인증이 다음 요청에서 거부된다. 로그아웃을 모든 복사된 쿠키의 전역 폐기라고 주장하지 않는다.
- API 응답은 `Cache-Control: no-store`. 업무 예외 400/403/404/409를 구별하고 예상하지 못한 오류는 안전한 메시지와 상관 ID만 노출한다. 현재 회비/매핑 후 선택적 동기화 결과가 null이면 204를 사용한다. 매핑 저장 후 동기화 실패라는 부분 성공은 유지한다.
- 자격증명은 전용 ADMIN 쓰기 경로만 제공한다. 자격증명 읽기·임의 설정 키·임의 감사 로그 쓰기 API는 없다. 설정/갱신 감사는 서버 작업에서 기록한다. 서버의 기존 자격증명 값은 조사하거나 로그로 출력하지 않았다.
- 기존 DB 스키마·EF 마이그레이션·Domain 규칙은 변경하지 않았다. 출석/통계/팀 배정/시트 해석을 브라우저에 재구현하지 않았다. 기본 날짜는 브라우저 현지 시간 대신 KST를 사용한다.
- 기존 `app.css`·아이콘·manifest를 유지했다. `app.css` SHA256: `e5ae36eebc2af3134be3e00eda05826b4a47780254c2cd7a1921775c5666f1de` (HEAD와 동일). 게시된 브라우저 `_framework`에 Infrastructure·EF·Google API·Identity EF 저장소 어셈블리가 없음을 확인했다.

## 실행·검증·배포

```bash
dotnet build
dotnet test
dotnet publish VolleyballClub.Web/VolleyballClub.Web.csproj -c Release -o artifacts/wasm-publish
docker build -t vicikyu-wasm-audit .
node scripts/verify-wasm-migration.cjs
```

- 서버 단독 실행: `dotnet run --project VolleyballClub.Web` (`localhost:5121`). 이 goal에서 운영 `5122`/Funnel/compose/영속 볼륨은 변경하지 않았다.
- Dockerfile은 Client csproj도 복사/복원하고 같은 Web 실행 파일에 WASM 정적 자산을 함께 게시한다. 기존 `/data` 볼륨·접속 주소는 유지한다. `.dockerignore`는 로컬 DB·진단/에이전트 산출물·node_modules·검증 산출물을 빌드 컨텍스트에서 제외한다.
- 브라우저 검증은 `.diag/pw/node_modules/playwright` 또는 `PLAYWRIGHT_MODULE`로 지정한 설치본, Edge 또는 `BROWSER_EXECUTABLE` Chromium이 필요하다. 자동 패키지 설치는 없다.
- 검증 스크립트는 임의 이름의 컨테이너/별도 bridge 네트워크·루프백 임의 포트·`/tmp` tmpfs DB를 생성한다. 호스트/운영 DB 볼륨을 마운트하지 않고 마지막에 컨테이너·네트워크를 삭제한다. 일반 bridge이므로 네트워크 자체의 air-gap을 주장하지 않는다.
- 서버 전용 `Verification` 환경은 정확한 임시 연결 문자열과 빈 Google 구성만 허용한다. 그 환경에서만 Sheets/Sync 테스트 대역을 주입하고 임시 시드를 만든다. Development/Production에서는 대역이 활성화되지 않는다. 실제 Sheets 통신을 이 검사로 검증한 것으로 간주하지 않는다.
- 서버 시작/브라우저 작업/Docker 명령/검증 전체에 시간 제한이 있다. 외부 강제 종료가 finally를 건너뛴 경우 이름·이미지·Verification 환경·tmpfs·네트워크를 확인해 소유한 임시 자원만 정리한다.
- 운영 반영·백업·롤백 절차는 [DOCKER.md](../DOCKER.md) 참조. 운영 반영은 별도 승인 후 Web 이미지 교체이며 `down -v`를 전환 절차로 사용하지 않는다.

## 검증 증거

원래 서비스 테스트 132개를 보존했다. HTTP/전송 테스트는 실 Identity 쿠키/CSRF, 임시 SQLite, Sheets 대역으로 소유권·401/403·토큰 회전·상태/역할/스탬프/비밀번호 무효화·업무 정상/오류·부분 성공·안전한 오류·nullable 전송을 검증한다. 비밀번호 변경 후 WASM 인증 상태/CSRF 갱신 회귀 테스트도 추가했다. 최종 원시 빌드·테스트 결과는 아래 파일이 기준이다.

| 증거 | 파일/결과 |
| --- | --- |
| 빌드 | `artifacts/wasm-build.log` — 기본 `dotnet build` 종료 0, 오류 0 |
| 테스트 | `artifacts/wasm-test.log` — 기존 132 + 신규 9 = **141 통과, 실패 0, 건너뜀 0**, 종료 0 |
| Release 게시 | `artifacts/wasm-publish.log`, `artifacts/wasm-publish/wwwroot/_framework/` — 서버와 WASM 자산, 종료 0 |
| Docker 빌드 | `artifacts/wasm-docker-build.log` — `vicikyu-wasm-audit` 이미지, 종료 0 |
| 브라우저 | `artifacts/wasm-browser-run.log`, `artifacts/wasm-browser/results.json` — 17경로 × 2뷰포트 직접 접근/새로고침, 34회 측정·스크린샷, 주요 흐름 8건, WASM 응답 5192건, 브라우저 오류 0, 종료 0 |
| 화면 | `artifacts/wasm-browser/*.png` — desktop Edge/Chromium `1280×900`, mobile 크기 `393×852`; 엔진 버전은 results.json |

브라우저 시간대는 `America/Los_Angeles`로 설정해 KST 업무 날짜와 화면/DTO 흐름을 확인했다. 실제 WASM 바이너리 응답을 관측하고 Server websocket·`/_blazor` 요청 및 화면 오류가 없음을 검사한다. 로그인 화면의 불필요한 스크롤/내비 부재, 가로 넘침, 기존 floating 내비의 CSS inset/고정 위치를 실제 화면 좌표로 측정한다. 내비를 화면 끝 0px로 바꾸지 않았다. 원래 시각적 동작을 기준으로 검증했다.

확인한 흐름: WASM 부원 추가 폼; 로그인·401/403 이동; OTP UI·개인 기록/달력·관리자 출석 보정; 게스트·랜덤 팀·확정·이동; 회비 생성/납부·학기 생성/수정/삭제; 시트 등록·키 쓰기·대역 갱신·감사·회원 매핑; 개인 색 저장; 활동 마감/재오픈; 비밀번호 변경·세션 무효화·재로그인·로그아웃. 전체 기능의 모든 조합을 E2E로 전수 검증했다고 주장하지 않으며 기존 업무 테스트와 함께 판단한다.

## 개발 서버와 한계

사용자가 이 저장소의 개발 서버 `5121` 중지·재시작을 승인했다. 기본 빌드 출력을 잠그던 Web 프로세스만 경로/포트를 확인해 중지했다. 최종 개발 서버는 게시 산출물을 `.diag/wasm-dev-runtime/`에 복사한 별도 실행본으로 재시작해 기본 build/publish 출력 파일을 잠그지 않는다. 작업 디렉터리는 기존 Web 디렉터리로 유지하며 `Sheets__SyncOnStartup=false`를 그 프로세스에만 적용했다. 정상 Development 자동 동기화 기본값은 true다. 이후 소스 수정은 이 복사 실행본에 자동 반영되지 않으므로 별도 재시작이 필요하다.

실제 iPhone/WebKit/standalone 앱·실제 Google Sheets·운영 HTTPS 배포는 검증하지 않았다. desktop Chromium의 모바일 뷰포트 결과를 실기기 해결 증거로 사용하지 않는다. 오프라인/PWA 서비스워커·새 실시간 동기화·UI 재설계는 추가하지 않았다. 독립 리뷰·지적 해소·감사 승인 전에는 완료가 아니다.
