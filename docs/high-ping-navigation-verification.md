# 고핑 홈 → MY 검증·반영 기록

[인테이크] 명확화 0건 · [부정검토] 이견 없음

**현재 상태: 자동 검증·Web 반영·MY 실기기 개선 확인 및 신선 컨텍스트 감사 리허설 7항목 PASS. 목표 감사 승인 대기. 다른 16개 경로는 사용자 추가 승인을 받은 별도 후속 범위이며 아직 해결됐다고 하지 않는다.**

## 승인 범위·변경

[조사](high-ping-navigation-investigation.md) 후 [문제/방향 승인](high-ping-navigation-decisions.md)을 각각 받고 제품 코드를 변경했다.

- `VolleyballClub.Client/Components/Pages/MyPage.razor`: 로딩 상태/공간 유지, 실제 값이 준비되기 전 임시값 차단, 기존 profile/stats/calendar GET의 `Task.WhenAll`, 초기 실패/재시도. 월 이동·색/비밀번호·인증/업무 API는 기존 계약 유지.
- `MyPage.razor.css`: MY에만 적용하는 정적 자리 표시. 새 애니메이션·JS 좌표 보정·캐시·통합 API 없음.
- `scripts/verify-wasm-migration.cjs`: 기존 전체 회귀를 유지하고 MY 정상/600ms 지연·공간/데이터·병렬·500 실패/재시도를 추가. 날짜는 시드 활동 날짜에서 얻으며 타임라인 기준점을 통일한다.
- 추가 사용자 승인 후 WebAssembly.Server만 `10.0.12 → 10.0.13` 보안 패치. .NET 10 유지, 다른 패키지/도메인/DB 스키마 변경 없음.

## Before / After (통제된 desktop Chromium)

| Before | After | Why |
| --- | --- | --- |
| 이름 공백·가입일 `0001.01.01`·`0년 0월`·가짜 `0회` | 명시적 대기 표시·자리 표시, 실제 데이터 후 값 표시 | 미완성 상태를 실제 회원/출석 정보처럼 보이지 않게 함 |
| 통계 카드가 늦게 생겨 달력 Y 약 159px 이동 | 같은 카드/현재 월 행 구조를 유지, 측정 이동 0px | 데이터 교체 시 불필요한 본문 재배치 제거 |
| profile → stats → calendar 순차 | 기존 읽기 3개 동시 시작 | 고지연 대기를 3번 누적하지 않음 |
| 요청당 +600ms에서 내용 준비 1924.3ms | 수정 후 759.6ms (모바일 크기 표본) | 동일 종류의 주입 조건에서 측정, 실기기 속도 보장은 아님 |
| 초기 실패가 페이지 오류로 이어질 수 있음 | 명시적 alert·다시 불러오기, 성공 후 실제 값 | 무한 로딩이나 잘못된 기본값 대신 회복 경로 |

원시 기록: `artifacts/high-ping-navigation/baseline.json`, `current.json`.
현재 표본: `393×852`/`1280×900`, 각각 추가 지연 0/600ms, warm 홈 완료 후 MY.
4조건 모두 달력 이동 0px. 모바일 +600ms API 시작 약 34.3/36.5/39.0ms, 각 응답 완료 약 715ms로 병렬을 확인했다. 캡처 PNG는 같은 디렉터리의 `pending-*`, `loaded-*`, `profile-fault-*`.

**desktop Edge/Chromium 모바일 크기이지 실제 iPhone/WebKit/홈 화면 앱 측정이 아니다.** 실제 iPhone API 수치나 0px를 측정했다고 주장하지 않는다. 픽셀/대기시간의 실기기 단정은 사용자 확인과 구분한다.

## 자동 검증·독립 리뷰

| 확인 | 원시 근거 |
| --- | --- |
| `dotnet build` 종료 0, 오류 0 | `artifacts/high-ping-navigation/build.log` |
| `dotnet test` 종료 0, 141 통과/실패 0/건너뜀 0 | `test.log` (동일 디렉터리), 기존 테스트 삭제/비활성화 없음 |
| `docker build -t vicikyu-wasm-audit .` 종료 0 | `docker-build.log`, 승인된 10.0.13 패치 후 NU1903 경고 없음 |
| `node scripts/verify-wasm-migration.cjs` 종료 0 | `verify.log`: 34 경로/뷰포트, 기존 주요 흐름 8건, 브라우저 오류 0 + 새 MY 4조건/오류·재시도 |
| 실제 후보 이미지 | `sha256:e9764365d2aaea64b71123373d280a9f9118a6778bd1b062b1b5dbdc174b23da` |
| 신선 컨텍스트 UI 코드 리뷰 | `.pi-subagents/artifacts/outputs/77de5c9d/.pi-subagents/high-ping-ui-review.md` — 해당 MY 코드/스타일에서 차단 이슈 없음, read-only |
| 회귀 작성/초기 실행 보고 | `.pi-subagents/artifacts/outputs/448ed1c2/.pi-subagents/high-ping-regression-report.md`; 최종 원시 로그는 부모 재실행 결과가 기준 |
| 변경 위생·진단 | `git diff --check` 종료 0, lens session blocking errors 없음. Razor/프로젝트 LSP 지원 한계는 CLI 컴파일로 확인 |
| 신선 컨텍스트 감사 리허설 | `.pi-subagents/artifacts/outputs/8cadd85c/.pi-subagents/high-ping-goal-rehearsal.md` — 원래 MY 목표 7항목 PASS, BLOCKERS none. 다른 16개 후속 경로와 구분 |

전체 WASM 이전 목표의 독립 보안 리뷰를 이 MY 리뷰로 대신 완료했다고 주장하지 않는다. 임시 DB/시드/Sheets 대역으로만 자동 업무 검증했고 운영 DB·실제 Sheets에 테스트 요청을 보내지 않았다.

## 운영 반영·백업·롤백

- 2026-10-14, 기존 `vicikyu-web-1`의 project/service/이미지/포트/볼륨을 확인 후 **검증된 이미지 ID를 Web에만 반영**했다. Funnel 설정은 변경하지 않았다.
- Web를 잠시 중지하여 DB 파일/WAL 등 `/data` 전체를 일관된 상태로 복사했고, 오프라인 백업에 Python SQLite `PRAGMA integrity_check`를 실행하여 `ok`를 얻었다. DB 파일 크기 249856바이트.
- 보호된 백업 경로: `.diag/deployment-backups/20261014152203/`. 현재 사용자/SYSTEM ACL로 제한하며 git/build context에서 제외된다. DB/인증 키/서버 비밀 내용은 로그나 문서에 출력하지 않았다.
- 기존 DataProtection 키 디렉터리도 백업 후 새 컨테이너가 시작하기 전에 복원했다. 코드·기존 쿠키 설정은 변경하지 않았다. 실제 사용자 로그인 상태가 유지되는지는 실기기 확인과 별개로 단정하지 않는다.
- 기존 이미지 `sha256:e5c050f2e6fe719a7243bb3dddb9efc3b2924116bd1bb0a7a575ac6c61476349`를 `vicikyu-web:pre-high-ping-20261014152203`로 보존했다.
- 절차: `.diag/deploy-high-ping.cjs prepare` → `backup` → `apply`. 동일 기존 Compose project로 Web만 create/start하고 새 컨테이너 시작 전 `/data` 마운트 동일성을 assert했다. `down -v`/볼륨 생성·초기화·DB 덮어쓰기는 하지 않았다.
- 반영 전후 `/data`는 **동일 `vicikyu_vbdata`**, 포트는 **동일 5122→8080**. Development/기존 자동 시작 동작과 Sheets 설정은 변경하지 않았고 수동 시트 갱신/운영 데이터 테스트를 하지 않았다.
- 롤백: `node .diag/deploy-high-ping.cjs rollback`. 이전 Web 이미지/인증 키를 복원하되 **동일 DB 볼륨을 계속 사용**한다. 정상 운영 중 새 기록을 잃지 않도록 DB 백업을 자동 덮어쓰지 않는다. DB 복원은 별도 사고 판단/승인 필요.
- 원시 반영 기록: `artifacts/high-ping-navigation/deployment.json`, private `.diag/high-ping-deployment-state.json`.

## 실제 주소 확인

2026-10-14T15:28Z 읽기 전용 확인:

- 공개 `https://bigsungg.taile671ea.ts.net`와 `localhost:5122` Login 200/WASM, 익명 auth/user 401, Web running.
- 두 주소의 새 Client 이름/해시 일치: `VolleyballClub.Client.6pn4rw5wcn.wasm`, `sha256-BuRYThL17L3atIELytkkDctSyGuRchfydPeWGnHejR4=`.
- 부트 로더 SHA256: `07e7739f30480e39259a5bfcc7de3f29f86a2131e5d8c794efcbbc0293e3bcae`.
- 실제 HTML의 `VolleyballClub.Web.094o1116o3.styles.css` 200 → `VolleyballClub.Client.ankapll7zp.bundle.scp.css` 200, MY 자리 표시 CSS 포함. 일반 `_content/...bundle.scp.css` 추정 경로가 아니라 HTML의 실제 fingerprint 링크를 확인했다.
- 실제 배포 `deployed.deps.json`에 `Microsoft.AspNetCore.Components.WebAssembly.Server/10.0.13` 확인.

## 실제 iPhone 최종 확인 — 사용자 확인 수신

사용자 기준: **iPhone 15 Pro / iOS 26.6.1 / LTE / 홈 화면 앱 / Funnel**.

수정본을 받도록 홈 화면 앱을 완전히 닫았다가 다시 연 후 홈의 내용이 준비되면 MY로 이동해 확인을 요청한다:

1. 빈 회원 정보/가입일 0001/0년 0월/가짜 0회 대신 대기 표현이 보이는가?
2. 통계·달력이 들어올 때 이전처럼 본문이 크게 밀리지 않는가?
3. 실제 값이 정상이며 네트워크 대기 중 하단 탭으로 이동할 수 있는가?

본 세션 `ask_user_question`, header `실기기 최종 확인`에서 위 동일 환경/수정본의 실제 확인 결과를 질문했다. 사용자 답변 원문:

> 다른 모든 페이지도 동일한 문제가 있어, 마이 페이지는 이제 괜찮아

MY가 이제 괜찮다는 실제 사용자 확인으로 기록한다. 실기기의 정량 API 시간/0px 측정은 아니므로 desktop 수치와 구분한다. 다른 모든 페이지에도 문제가 있다는 보고는 **MY 목표의 해결 범위에 포함시키거나 해결됐다고 하지 않는다**. 후속 범위 질문에서 사용자는 `인증·오류 화면까지 전체 16개`를 선택했다. 이는 MY 외 전체 경로에 대한 추가 조사/수정 승인으로 기록하며, 데이터 초기 로딩과 인증/부트/오류 화면은 원인을 구분해서 후속 작업으로 진행한다. 원래 MY 목표의 감사 리허설은 완료되어 MY 목표를 감사에 제출한다.
