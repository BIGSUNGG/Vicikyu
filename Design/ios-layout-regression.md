# iOS/PWA 레이아웃 회귀 조사

## 상태와 수용 기준

- 재현 기기: iPhone 15 Pro / iOS 26.6.1 — **사용자 보고**, 직접 기기 정보 조회 아님.
- 실제 사용자 주소: `https://bigsungg.taile671ea.ts.net/`.
- 키보드 미표시·내용이 들어가는 인증 화면: 메뉴 예약 여백/추가 세로 스크롤 없음.
- 짧은 폼 뒤의 아이보리 바탕은 유지하며 화면 전체를 채움. 긴 내용/키보드로 줄어든 공간에서는 필요한 스크롤 허용.
- 메뉴 border-box 하단 간격 10px, bottom SafeArea 가산 없음. 6px 그림자는 간격 측정에서 제외.
- 상단 full-bleed 배경과 `env(safe-area-inset-top)` 처리는 보존.
- **기계 검증 통과 / iPhone 설치 앱 최종 확인 대기. 전체 목표 완료 아님.**

## 입증된 사실과 미확인 가설

| 항목 | 근거 | 판단 |
| --- | --- | --- |
| absolute + JS 좌표 추적이 스크롤 중 늦게 따라옴 | 기존 `App.razor`는 scroll/rAF에서 top을 변경. 아래 기준선 측정에서 gap 460px 발생 | 확인된 회귀. 좌표 추적을 삭제하고 CSS fixed로 복원 |
| 탭 이동 전후 배치 방식이 달랐음 | 같은 브라우저에서 최초 absolute → 탭 이동 직후 fixed → 다음 scroll absolute | 확인됨. 이를 무조건 WebKit 재계산으로 설명했던 이전 주장은 철회 |
| 검증한 서버와 사용 서버가 달랐음 | `start.ps1`: Docker compose 빌드 + Funnel 5122. 이전 검증은 별도 dotnet 서버 5121 | 확인됨. 실제 앱은 Docker 재빌드 후 공개 주소에서 검증해야 함 |
| 인증 레이아웃에 사이드 열이 남음 | 변경 전 1024px 화면 contentLeft=310, width=666, 중심=643(화면 중심512과 불일치) | 확인됨. 메뉴 레이아웃의 grid를 AuthLayout에 적용하지 않도록 범위 분리 |
| iPhone 최초 fixed 메뉴 위치 이상 | 사용자 관찰. 실제 iOS viewport/nav rect·영상 아직 없음 | **원인 미확정**. 아래 외부 이슈를 현재 버전의 확정 원인으로 대체하지 않음 |
| iPhone 인증 화면의 불필요한 스크롤 | 사용자 관찰. 데스크톱의 모바일 크기에서는 변경 전후 overflow=0 | **실기기 확인 필요**. 추정으로 viewport meta·타이머 보정을 추가하지 않음 |

### 수정 전 로컬 기준선 원문 발췌

엔진: 설치된 Edge/Chromium. iPhone UA와 standalone 플래그로 **이전 코드 분기만 실행**했으며 iOS WebKit 재현이 아님.

```json
{"label":"home-initial-render","nav":{"position":"absolute","bottom":842,"gap":10,"inlineTop":"776px"}}
{"label":"records-after-tab","nav":{"position":"fixed","bottom":842,"gap":10,"inlineTop":""}}
{"label":"records-scroll-650","scrollY":650,"nav":{"position":"absolute","bottom":392,"gap":460,"inlineTop":"976px"}}
{"label":"login-1024x900","overflow":0,"mainPaddingBottom":"48px","contentLeft":310,"contentWidth":666}
```

## 공식 자료

1. [WebKit Bug 206228](https://bugs.webkit.org/show_bug.cgi?id=206228#c1):
   > Scroll events are, per spec, asynchronous. ... you should never rely on them to reposition things. Use position:sticky, position:fixed etc instead.

   JS 스크롤 좌표 추적을 제거하는 근거. 모든 최초 위치 문제의 원인이라는 뜻은 아님.
2. [WebKit Bug 301172](https://bugs.webkit.org/show_bug.cgi?id=301172): 보고 기기는 iPhone 16e / iOS 26.0.1이며, 26.1에 대해:
   > I'm seeing resolution with regard to the initial symptoms.

   이후 별도 safe-area 증상도 보고됨. 이 보고만으로 사용자 iOS 26.6.1의 원인을 확정할 수 없음.
3. [WebKit: Designing Websites for iPhone X](https://webkit.org/blog/7929/designing-websites-for-iphone-x/):
   > ... you can set viewport-fit to cover.
   > ... selectively apply padding to elements that contain important content ...

   화면 배경의 확장과 중요한 내용의 인셋은 분리. 해결된 상단 안전영역 처리를 되돌릴 이유 없음.

## 실제 배포 경로 확인

`start.ps1` → `docker compose up -d --build` → 웹 컨테이너 포트5122 → Tailscale Funnel.
개발 서버5121의 재시작은 이 앱을 갱신하지 않음.

반영 전 HTTP 응답:

```text
5121: app.tq636p2iqe.css; positionNav=false; auth-shell=true
Tailscale: app.o5cpzsutff.css; positionNav=true; auth-shell=false
```

사용자 승인 후 실행:

```text
docker compose up -d --no-deps --build web
EXIT=0
Container vicikyu-web-1 Recreated / Started
Data mount: vicikyu_vbdata -> /data (유지)
```

반영 후 HTTP 응답(세 주소 모두 200):

```text
5121 / 5122 / https://bigsungg.taile671ea.ts.net
CSS asset: app.tq636p2iqe.css
CSS SHA256: e5ae36eebc2af3134be3e00eda05826b4a47780254c2cd7a1921775c5666f1de
positionNav=false; auth-shell=true; safe-area-inset-top=true; safe-area-inset-bottom=false
```

DB 컨테이너/볼륨 삭제, 인증 처리, 프록시 설정은 변경하지 않음.

## 변경 범위

- `App.razor`: 실패한 absolute/scroll/rAF 위치 추적 전체 삭제. 임시 meta 토글·reflow·타이머 추가 없음.
- `app.css`: fixed bottom10px 유지. 인증 shell 배경은 전체 폭, 내용은 기존 최대560px. 메뉴용 태블릿 grid를 인증 화면에서 제외.
- `AuthLayout.razor`: 인증 전용 selector로 레이아웃을 격리. Login/Logout 메뉴 숨김 유지.
- `AGENTS.md`: 기기별 회귀의 측정·출처 적용 버전·실기기 완료 조건 보강.
- `scripts/verify-ios-layout.cjs`: 실제 스크롤 발생, 즉시/두 프레임 뒤 위치, 인증 overflow/폭/긴 내용 접근, 탭 이동 완료, 일반 태블릿 레이아웃을 검증.

## 재실행

Node + 기존 `playwright-core`, 설치된 Edge/Chrome이 필요. 새 앱 런타임 의존성은 추가하지 않음.
이 환경에서는 기존 Bun 캐시의 playwright-core를 사용했다. 다른 환경은 설치된 모듈 경로를 지정한다.

```powershell
$env:PLAYWRIGHT_MODULE = 'C:/Users/DAESUNG/.bun/install/cache/playwright-core@1.64.0@@@1'
$env:BROWSER_EXECUTABLE = 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe'
$env:LAYOUT_BASE_URL = 'https://bigsungg.taile671ea.ts.net'
$env:LAYOUT_LOGIN_PASSWORD = '<개발용 계정 비밀번호>'
node scripts/verify-ios-layout.cjs
```

**검사 제한:** 공개 앱 로그인 응답은 기존 프록시 구성에서 HTTP Location을 반환한다.
검사는 `maxRedirects:0`으로 로그인 쿠키만 받은 뒤 지정된 HTTPS 주소로 이동한다.
이는 UI 배치 검사이며, 공개 로그인 리다이렉트의 정상 동작을 인증하는 검사가 아니다.
실제 iOS 키보드·관성 스크롤·런치 전환도 데스크톱 검사로 인증하지 않는다.

## 실행 결과 (2026-10-13)

```text
dotnet build exit=0
경고 0개
오류 0개
dotnet test exit=0
통과! - 실패: 0, 통과: 132, 건너뜀: 0, 전체: 132
PUBLIC BROWSER CHECK EXIT=0
Login response: 302; redirect scheme=http:
PASS — desktop Chromium geometry; iPhone verification is still required
```

공개 HTTPS 앱 측정 출력에서 발췌:

| 검사 | 실제 측정 |
| --- | --- |
| Login393×852 / 393×640 | overflow0, padding-bottom16, nav없음, 배경 폭=viewport폭 |
| Login768×852 | overflow0, shellWidth768, contentWidth560 |
| Login1024×900 | overflow0, shellWidth1024, contentLeft232, contentWidth560(중심512) |
| Login393×480 | scrollHeight590, clientHeight480, scrollY44, 로그인 버튼 화면 내 접근 가능 |
| Logout393×852 / 1024×900 | overflow0, nav없음, 배경 폭=viewport폭, contentWidth≤560 |
| 최초 홈 / 기록 탭 | position=fixed, bottom842, visualHeight852, gap10 |
| scrollY200 / 650 / 0, 즉시 및 두 프레임 후 | 실제 scrollY 일치, position=fixed, gap10 모두 유지 |
| 일반 홈1024×900 | bottom-nav숨김, side-nav노출, contentLeft310(기존 열 유지) |

## 실기기 결과 — 미확인

iPhone 15 Pro의 **실제 Tailscale 홈 화면 앱**에서 다음 확인이 필요하다.

- 조작 전 첫 화면부터 메뉴 하단10px; 탭 이동으로만 고쳐지는 현상 없음.
- 드래그/관성 스크롤에서 메뉴가 더디게 따라오지 않음.
- 내용이 들어가는 Login/Logout에서 불필요한 여백/스크롤 없음.
- 아이보리 배경이 화면 끝까지 채워지고 상단 펀치홀 처리 유지.

아직 사용자 성공 확인을 받지 않았다. 이전 설치 앱 화면이 계속 다르면 첫 화면과 탭 이동 후 화면의 스크린샷/짧은 영상을 요청하고, URL·배포본을 다시 대조한다. 일부 개선 또는 재현 지속은 완료로 간주하지 않는다.
