# Docker 실행 가이드 (VolleyballClub)

Web(Blazor) + PostgreSQL을 docker-compose로 한 번에 실행하는 가이드.

## 구성

| 파일 | 역할 |
| --- | --- |
| `Dockerfile` | Web 앱 멀티스테이지 빌드(sdk:10.0 → aspnet:10.0) |
| `docker-compose.yml` | `db`(postgres:18, 볼륨 `pgdata`) + `web`(호스트 5122 → 컨테이너 8080) |
| `.dockerignore` | 빌드 컨텍스트 제외(bin/obj, Tests, Design 등) |

- 호스트 포트 5121은 로컬 개발 서버(`dotnet run`) 전용 — 컨테이너는 5122를 사용해 충돌 방지

- Web은 Development 모드로 기동 → DB 마이그레이션 + 시드 자동 적용
- 연결 문자열·관리자 비밀번호는 compose의 환경변수로 주입
  (`ConnectionStrings__DefaultConnection`, `Seed__AdminPassword`)
- DB 데이터는 `pgdata` 볼륨에 유지 (`docker compose down`해도 유지)

## 사전 요건

```bash
docker info   # Docker Desktop 실행 확인 — 실패하면 Docker Desktop을 먼저 실행
```

## 빌드 · 실행 · 중지

```bash
docker compose up -d --build   # 빌드 + 백그라운드 기동 (db healthy 후 web 시작)
docker compose ps              # 상태 확인 (db: healthy, web: Up)
docker compose logs -f web     # Web 로그 실시간 확인
docker compose down            # 중지 (데이터 유지)
docker compose down -v         # 중지 + DB 데이터 초기화 (시드부터 재생성)
```

## 접속

- 로컬: <http://localhost:5122/Account/Login>
- LAN(같은 네트워크 기기): `http://<PC-IP>:5122/Account/Login`
  - Windows 방화벽 인바운드 규칙 필요 시:

  ```powershell
  netsh advfirewall firewall add rule name="VolleyballClub Docker" dir=in action=allow protocol=TCP localport=5122
  ```

- 홈(`/`)은 로그인 필요로 `/Account/Login`으로 302 리다이렉트됨 (정상 동작)

### 관리자 계정

- ID: `admin`
- 비밀번호: compose의 `Seed__AdminPassword` 값 (현재 `Admin1234!` — 로컬 개발과 동일)
- 변경: `docker-compose.yml`의 `Seed__AdminPassword` 수정 후 `docker compose down -v && docker compose up -d --build`
  (시드는 첫 기동에만 적용되므로 볼륨 초기화 필요)

## 인터넷 공개 — Tailscale Funnel

공유기 설정 없이 HTTPS로 외부 공개. 무료 Tailscale 계정 필요.

```bash
winget install --id Tailscale.Tailscale     # 설치 (1회)
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
| `db` 컨테이너 종료(마운트 오류) | postgres:18은 볼륨을 `/var/lib/postgresql`에 마운트해야 함 (compose에 이미 반영) |
| Web 로그에 `libgssapi_krb5` 오류 | Npgsql 런타임 요구 라이브러리 — Dockerfile에 이미 설치됨 |
| 5122 포트 접속 불가(LAN) | Windows 방화벽 규칙 추가 (위 netsh 명령) · 기존 5121 규칙이 funnel/LAN 용도라면 5122로 재생성 |
