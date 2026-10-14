# WASM 전환 독립 리뷰 상태

**상태: 보류 — 신선 컨텍스트 리뷰의 최종 판정/지적 해소가 아직 없다. 미해결 차단 이슈 0건으로 선언할 수 없다.**

## 실행한 독립 리뷰와 원본

아래는 성공한 리뷰 보고서가 아니라 실패한 실행의 원본이다. 부분적인 읽기 도구 사용이나 인테이크 표기를 CLEAN 판정으로 취급하지 않는다.

| 검토 범위 | 모델 | 실행 결과 | 원본 산출물 |
| --- | --- | --- | --- |
| API/인증·권한·CSRF·공개 계약·계층 | Codex 현재 계정 | 사용량 한도 오류, 최종 보고 없음 | `.pi-subagents/artifacts/fbd65765_reviewer_0_output.md`, 같은 접두사의 `_transcript.jsonl` |
| UI/기능·HTTP 클라이언트·경로·의존 경계 | Codex 현재 계정 | 사용량 한도 오류, 최종 보고 없음 | `.pi-subagents/artifacts/7aa750e8_reviewer_0_output.md`, 같은 접두사의 `_transcript.jsonl` |
| 전체 신규 모듈·API·보안·회귀 | Gemini 3.1 Pro | 429: 해당 모델 free-tier limit 0, 검토 시작 불가 | `.pi-subagents/artifacts/69a0a68f_reviewer_0_output.md` |
| 동일 목적, 별도 설정된 모델 | Gemini 2.5 Pro | 404: 신규 사용자에게 제공되지 않는 모델, 검토 시작 불가 | `.pi-subagents/artifacts/0978b284_reviewer_0_output.md` |

실행 로그는 현재 로컬 작업 공간에 보존된다. 모델 목록에 이름이 표시된다는 사실만으로 실제 호출 가능성을 주장하지 않는다. 같은 한도 오류 호출을 반복하거나 실패한 리뷰를 자가 검토로 대체해 독립 리뷰 계약을 만족시켰다고 하지 않는다.

## 부모의 검증과 수정 (독립 리뷰 아님)

- 개인 API가 인증된 서버 회원 ID를 사용하고 ADMIN API가 별도 인가를 요구하는 구조, CSRF·no-store·예외 구별·서버 전용 의존을 코드/HTTP 테스트로 확인했다.
- 자신의 비밀번호 변경 후 이전 서버 인증은 이미 무효가 되는데 브라우저 표시 상태가 남는 경로를 보완했다. `Client/Services/MemberClient.cs`가 성공 후 인증 상태·CSRF를 갱신한다. `HttpApiTests.Own_Password_Change_Refreshes_Browser_Identity_And_Csrf`와 실제 WASM 비밀번호 변경/재로그인 흐름을 추가/재실행했다.
- 브라우저 게시 자산에 Infrastructure·EF·Google API·Identity EF 저장소 어셈블리가 없고 app.css가 원래 HEAD와 같음을 확인했다. Domain/EF migration 변경은 없다.
- Docker 테스트 환경은 서버 전용 명시적 `Verification` 환경·고정 `/tmp` DB·빈 Google 구성에 제한했다. E2E는 테스트 대역과 disposable DB를 사용하고 운영 볼륨을 마운트하지 않는다. 강제 종료된 임시 컨테이너 1개는 이미지·환경·DB·마운트·네트워크를 확인 후 소유 자원만 정리했다.
- 빌드·141개 테스트·Release 게시·Docker 빌드·17경로 × 2뷰포트/8흐름 WASM 실행은 통과했다. 원시 증거는 `artifacts/wasm-*.log`, `artifacts/wasm-browser/results.json`, PNG에 있다. 이는 독립 코드 리뷰의 대체 증거가 아니다.

## 남은 조치

1. 호출 가능한 리뷰 모델/계정을 확보하거나 Codex 사용량 복구 후 기존 실패 리뷰 세션을 재개한다. 새 Verification 환경 및 비밀번호 변경 후 브라우저 인증 갱신을 포함한 **현재 최종 트리**를 읽도록 안내한다.
2. 신선 컨텍스트에서 신규 Client 모듈·공개 HTTP 계약·인증/권한/CSRF·계층 경계·기능 보존을 검토하고 원본 판정/지적을 남긴다.
3. 근거가 있는 지적을 처리하고 관련 테스트/브라우저 검증을 다시 실행한다. 이 문서에 원본 경로·각 지적의 처리·미해결 차단 이슈 수를 실제 판정에 따라 기록한다.
4. 독립 감사 리허설 후 목표 감사에 제출한다. 그 전에는 완료 도구를 호출하지 않는다.

운영 배포·실기기 iPhone·외부 Sheets 검증을 수행한 것으로 확대하지 않는다. 목표의 범위와 검증 계약은 변경하지 않았다.
