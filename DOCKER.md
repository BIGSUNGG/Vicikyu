# Docker 실행 가이드 (VolleyballClub)

Web(ASP.NET API + Blazor WebAssembly 정적 자산) + SQLite를 동일 출처·단일 컨테이너로 실행하는 가이드. 브라우저 UI(로그인·로그아웃 포함)는 WASM이며 Identity·권한 검증·DB·Google Sheets는 서버에서 처리한다.

## 구성

| 파일 | 역할 |
| --- | --- |
| `Dockerfile` | Client 포함 Web 앱 멀티스테이지 빌드(sdk:10.0 → aspnet:10.0) |
| `docker-compose.yml` | `web`(호스트 5122 → 컨테이너 8080) + SQLite 데이터 볼륨 `vbdata` + Google 서비스계정 마운트(`.secrets/vicikyu-sa.json`) — 시트 탭 캐시는 프로세스 내 메모리(외부 캐시 불필요) |
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

Docker·cloudflared 설치는 `scripts\setup.ps1`(Linux `./scripts/setup.sh`)이 자동화한다.

## 빌드 · 실행 · 중지

```bash
docker compose up -d --build   # 빌드 + 백그라운드 기동
docker compose ps              # 상태 확인 (web: Up)
docker compose logs -f web     # Web 로그 실시간 확인
docker compose down            # 중지 (데이터 유지)
docker compose down -v         # 중지 + DB 파일 초기화 (시드부터 재생성)
```

> 위 기동 절차는 `scripts\run.ps1`(Linux `./scripts/run.sh`)이 터널 등록·헬스체크까지 자동 수행한다.

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

## 운영 스크립트 — setup · backup · restore · run

`scripts/` 에 4개 명령이 있다. Windows는 `.ps1`(더블클릭용 `.bat` 래퍼 포함), Linux는 `.sh`.

| 명령 | 역할 |
| --- | --- |
| `setup` | Docker·cloudflared 설치(멱등 — 이미 있으면 건너뜀) + 필수 파일 검사 |
| `backup` | web 정지(WAL 무결성) → DB 볼륨 tar + 구글 시크릿 + 터널 토큰 + manifest(sha256 체크섬) → web 자동 재개. `--image` 시 이미지 tar 추가 |
| `restore` | 백업 폴더(기본 `backups/` 최신, 경로 인자 가능) 체크섬 검증 → 시크릿·토큰 배치 → 볼륨 교체 → 기동·검증까지 자동 |
| `run` | 컨테이너 기동(이미지 없으면 빌드) + Cloudflare 터널 서비스 + 헬스체크. `--funnel`(Tailscale 병행) / `--no-tunnel` |

```powershell
powershell -ExecutionPolicy Bypass -File scripts\backup.ps1                 # 백업
powershell -ExecutionPolicy Bypass -File scripts\restore.ps1                # 최신 백업 복원+기동
powershell -ExecutionPolicy Bypass -File scripts\restore.ps1 D:\usb\vicikyu-20261027-1530
```

```bash
./scripts/setup.sh && ./scripts/run.sh          # 최초
./scripts/backup.sh --image                     # 백업(이미지 포함)
./scripts/restore.sh /mnt/usb/vicikyu-20261027-1530
```

### 서비스 이전 — 새 PC 절차

레지스트리 없이 로컬 빌드로 운영하므로 **백업 폴더 + 리포지토리**를 전달한다(`docker export`는 포트·볼륨·env가 누락되어 사용하지 않는다).

1. 원본 PC: `backup` 실행 → `backups/vicikyu-타임스탬프/` 폴더를 통째로 전달(USB/scp)
2. 새 PC: 리포지토리 확보(git clone 또는 복사) → 받은 폴더를 `backups/` 에 붙여넣기
3. `setup` 1회 실행(Docker·cloudflared 설치)
4. `restore` 실행 — 체크섬 검증, 시크릿·토큰 배치, 볼륨 복원, 기동·검증까지 자동
5. 이후 재기동은 `run` 만 실행하면 복원된 상태 그대로 서빙된다

백업 폴더에는 DB·구글 시크릿·터널 토큰 전부가 들어 있다. 보관·전송에 주의하고 git에 커밋하지 않는다(`backups/` 는 gitignore). 백업 기본값은 이미지 tar를 제외하므로 새 PC에서 재빌드된다 — 동일 아키텍처(amd64)에서 이미지까지 그대로 옮기려면 `--image` 로 백업할 것.

### ARM 대상 — Raspberry Pi 등 arm64

Windows에서 빌드한 이미지는 amd64라 Pi에서 실행되지 않는다. **볼륨·시크릿·리포지토리는 위 절차 그대로** 전달하고 이미지만 아래 둘 중 하나로 바꾼다.

```bash
# 방법 1: Pi에서 직접 재빌드 (느림, ~10-25분)
docker compose -p vicikyu up -d --build

# 방법 2: 원본 Windows PC에서 크로스빌드 후 docker save (이후 절차 동일)
docker buildx build --platform linux/arm64 -t vicikyu-web:latest .
```

- OS는 64비트 필수: `uname -m` → `aarch64` 확인. Dockerfile·compose는 수정 불필요
- SQLite WAL을 SD카드에 두면 마모·전원 단절 손상 위험 — **USB3 SSD 부팅 권장**, 불가 시 고내구 A2 카드 + 종료 전 `docker compose stop web`
- Pi에서 Development 기동 시 마이그레이션+시드에 1~2분 소요될 수 있음 (정상)

### 수동 절차 (참고 — 스크립트 고장 시)

#### 원본 PC — 백업

```powershell
cd C:\Projects\Vicikyu

docker volume ls                     # vicikyu_vbdata 확인
docker images                        # vicikyu-web:latest 확인

# SQLite WAL 무결성을 위해 쓰기 정지 후 백업 (down -v 절대 금지 — 데이터 삭제)
docker compose stop web

docker save -o vicikyu-web.tar vicikyu-web:latest

docker run --rm -v vicikyu_vbdata:/data -v ${PWD}:/backup alpine `
  tar cvf /backup/vbdata.tar -C /data .

# 원본 서비스 복구(계속 운영할 경우)
docker compose start web
```

#### 새 PC — 복원

```powershell
docker info                          # Docker Desktop 실행 확인

# 리포지토리 준비 후 루트에서 (폴더명은 달라도 됨 — -p vicikyu가 프로젝트명 고정)
git clone <리포지토리>
cd Vicikyu

# 시크릿 배치 — compose가 /config/vicikyu-sa.json:ro로 마운트
mkdir .secrets; copy <전달경로>\vicikyu-sa.json .secrets\

docker load -i vicikyu-web.tar

# 볼륨 생성 + 복원 (이름이 vicikyu_vbdata여야 compose가 찾음)
docker volume create vicikyu_vbdata
docker run --rm -v vicikyu_vbdata:/data -v ${PWD}:/backup alpine `
  sh -c "cd /data && tar xvf /backup/vbdata.tar"

# 기동 — --build를 붙이면 재빌드되므로 로드한 이미지를 쓸 때는 붙이지 않음
docker compose -p vicikyu up -d
```

폴더명이 달라지면 compose가 `<폴더명>_vbdata` 새 빈 볼륨을 만들어 **초기화된 것처럼 보이는** 가장 흔한 사고가 발생한다. 항상 `-p vicikyu`로 고정한다.

#### 검증

```powershell
docker compose -p vicikyu ps        # web: Up
docker compose -p vicikyu logs web  # 마이그레이션 적용 없음 확인
```

- <http://localhost:5122/Account/Login> — **기존 계정**으로 로그인되는지 확인 (새 시드 admin이 아니라)
- 부원 수·최근 활동이 원본과 일치하는지 대조
- LAN 접속: 방화벽 규칙(위 netsh 명령), 외부 공개: Tailscale Funnel(위 절) — 장비명이 달라지므로 공개 URL도 달라짐

## 문제 해결

| 증상 | 원인 · 조치 |
| --- | --- |
| `docker compose up` 시 daemon 연결 실패 | Docker Desktop 미실행 → 실행 후 `docker info`로 확인 |
| 5122 포트 접속 불가(LAN) | Windows 방화벽 규칙 추가 (위 netsh 명령) · 기존 5121 규칙이 funnel/LAN 용도라면 5122로 재생성 |
| DB 초기화 후 재시작 | `docker compose down -v && docker compose up -d --build` (시드부터 재생성) |
| 이전 후 부원·계정이 없음(초기화처럼 보임) | 프로젝트명 불일치로 새 빈 볼륨 생성 → `docker volume ls` 확인 후 위 «서비스 이전» 절로 복원 |
