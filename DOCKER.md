# Docker 실행 가이드 (VolleyballClub)

Web(ASP.NET API + Blazor WebAssembly 정적 자산) + SQLite를 동일 출처·단일 컨테이너로 실행하는 가이드. 브라우저 UI(로그인·로그아웃 포함)는 WASM이며 Identity·권한 검증·DB·Google Sheets는 서버에서 처리한다.

## 구성

| 파일 | 역할 |
| --- | --- |
| `Dockerfile` | Client 포함 Web 앱 멀티스테이지 빌드(sdk:10.0 → aspnet:10.0) |
| `docker-compose.yml` | `web`(호스트 5122 → 컨테이너 8080) + `redis`(시트 탭 읽기 캐시, 휘발성) + SQLite 데이터 볼륨 `vbdata` |
| `.dockerignore` | 빌드 컨텍스트 제외(bin/obj, Tests, Design 등) |

- 호스트 포트 5121은 로컬 개발 서버(`dotnet run`) 전용 — 컨테이너는 5122를 사용해 충돌 방지
- Web은 Development 모드로 기동 → DB 마이그레이션 + 시드 자동 적용
- 연결 문자열·관리자 비밀번호는 compose의 환경변수로 주입
  (`ConnectionStrings__DefaultConnection: Data Source=/data/VolleyballClub.db`, `Seed__AdminPassword`)
- DB 데이터는 `vbdata` 볼륨(`/data`)에 유지 (`docker compose down`해도 유지)

## 사전 요건

```bash
docker info   # Docker Desktop 실행 확인 — 실패하면 Docker Desktop을 먼저 실행
```

## 빌드 · 실행 · 중지

```bash
docker compose up -d --build   # 빌드 + 백그라운드 기동
docker compose ps              # 상태 확인 (web: Up)
docker compose logs -f web     # Web 로그 실시간 확인
docker compose down            # 중지 (데이터 유지)
docker compose down -v         # 중지 + DB 파일 초기화 (시드부터 재생성)
```

## 접속

- 로컬: <http://localhost:5122/Account/Login>
- LAN(같은 네트워크 기기): `http://<PC-IP>:5122/Account/Login`
  - Windows 방화벽 인바운드 규칙 필요 시:

  ```powershell
  netsh advfirewall firewall add rule name="VolleyballClub Docker" dir=in action=allow protocol=TCP localport=5122
  ```

- UI 경로는 HTTP 200 WASM 셸을 제공한다. 비로그인 사용자의 보호 화면 접근은 클라이언트가 `/Account/Login`으로 이동시킨다. API는 별도로 실제 401/403을 반환한다.
- WASM 자산과 `/api/*`는 같은 출처를 사용한다. 프런트 별도 배포·CORS·Blazor Server circuit은 사용하지 않는다.

### 관리자 계정

- ID: `admin`
- 비밀번호: compose의 `Seed__AdminPassword` 값 (현재 `Admin1234!` — 로컬 개발과 동일)
- `Seed__AdminPassword`는 최초 시드에만 적용된다. 기존 계정의 비밀번호는 앱의 비밀번호 변경/관리자 부원 관리로 변경한다. 비밀번호 변경이나 WASM 전환을 위해 운영 데이터 볼륨을 초기화하지 않는다.

## WASM 전환 검증·반영

```bash
dotnet build
dotnet test
dotnet publish VolleyballClub.Web/VolleyballClub.Web.csproj -c Release -o artifacts/wasm-publish
docker build -t vicikyu-wasm-audit .
node scripts/verify-wasm-migration.cjs
```

- 마지막 명령은 `Verification` 환경·임시 `/tmp` SQLite·별도 컨테이너/네트워크·루프백 임의 포트를 사용하고 종료 시 정리한다. Google 자격증명/시트 ID는 비우고 Sheets 테스트 대역을 사용한다. Chromium은 실제 iPhone이 아니다.
- 브라우저 도구: 기존 `.diag/pw/node_modules/playwright` 또는 `PLAYWRIGHT_MODULE`로 지정한 설치본과 Edge가 필요하다. `BROWSER_EXECUTABLE`로 Chromium 실행 파일을 지정할 수 있다. 스크립트는 패키지를 자동 설치하지 않는다.
- 증거: `artifacts/wasm-browser/results.json`·PNG·실행 로그. 상세 경로/기능 대응 및 미검증 한계는 [전환 문서](docs/blazor-wasm-migration.md) 참조.
- 운영 반영은 이 목표의 범위 밖이다. 별도 승인과 SQLite 일관된 백업 후 기존 포트·연결 문자열·`vbdata` 마운트를 유지한 채 `docker compose up -d --no-deps --build web`으로 Web만 교체한다. `down -v`는 마이그레이션 절차가 아니다.
- 롤백 시 이전 Web 이미지로 되돌리고 동일한 DB 볼륨을 유지한다. 이번 전환에는 DB 스키마/EF 마이그레이션 변경이 없다.
- 개발 검증 중 자동 외부 시트 동기화를 막으려면 실행 프로세스에만 `Sheets__SyncOnStartup=false`를 설정한다. 정상 Development 시작의 기본값은 true로 유지된다.

## 인터넷 공개 — Tailscale Funnel

공유기 설정 없이 HTTPS로 외부 공개. 무료 Tailscale 계정 필요.

```bash
winget install -id Tailscale.Tailscale     # 설치 (1회)
tailscale login                             # 브라우저 인증 (1회)
tailscale funnel --bg 5122                  # 터널 개시 (백그라운드 모드 필수)
tailscale funnel status                     # 공개 URL 확인
tailscale funnel --https=443 off            # 터널 중지
```

- 공개 URL 형식: `https://<장비명>.<tailnet>.ts.net/` (최초 `funnel` 실행 시 활성화 승인 페이지가 뜰 수 있음)
- 접속 확인: `curl -s -o /dev/null -w "%{http_code}" https://<url>/Account/Login` → `200`
- 주의: 공개 상태에서는 기본 시드 비밀번호가 아닌 강한 `Seed__AdminPassword`를 사용할 것

## 문제 해결

| 증상 | 원인 · 조치 |
| --- | --- |
| `docker compose up` 시 daemon 연결 실패 | Docker Desktop 미실행 → 실행 후 `docker info`로 확인 |
| 5122 포트 접속 불가(LAN) | Windows 방화벽 규칙 추가 (위 netsh 명령) · 기존 5121 규칙이 funnel/LAN 용도라면 5122로 재생성 |
| DB 초기화 후 재시작 | `docker compose down -v && docker compose up -d --build` (시드부터 재생성) |
