# Cloudflare 실서비스 배포 (vicikyu.site)

개인 테스트용 Tailscale Funnel(`scripts/run.ps1 -Funnel`)과 별도의 **실서비스 경로**.
Cloudflare는 ASP.NET을 실행하지 않으므로, 앱은 기존처럼 이 PC의 Docker(호스트 5122)에서
구동되고 Cloudflare Tunnel이 `https://vicikyu.site`로 외부에 공개한다.
서버 PC가 꺼지면 서비스도 중단된다(Funnel과 동일).

## 구조

```text
사용자 기기 → https://vicikyu.site (Cloudflare DNS+프록시)
                    ↓ Cloudflare Tunnel (cloudflared Windows 서비스)
              이 PC Docker web (호스트 5122 → 컨테이너 8080, SQLite vbdata)
```

## 최초 1회 설정 (사용자 수동)

1. **Cloudflare 가입·도메인 추가**: dash.cloudflare.com 가입 → `vicikyu.site` 추가(Free 플랜)
2. **가비아 NS 변경**: Cloudflare가 보여주는 2개 네임서버를 가비아 도메인 관리 → 네임서버 설정에 입력
3. **터널 생성·토큰 저장**: Cloudflare Zero Trust → Networks → Tunnels → Create tunnel
   (cloudflared 커넥터) → 발급된 토큰 문자열(EYJ...)을 `.cloudflare\tunnel-token.txt`에 저장
4. **공개 호스트명 지정**: 같은 터널의 Public Hostname에
   `vicikyu.site` → Service `HTTP://localhost:5122` 추가

## 기동 · 중지

```powershell
.\scripts\run.ps1               # Docker web 기동 + cloudflared 서비스 등록·시작 + 헬스체크
Stop-Service cloudflared         # 터널 중지 (web 컨테이너는 유지)
```

cloudflared는 Windows 서비스로 등록되어 PC 재부팅 후에도 자동 기동된다.
Docker web이 꺼져 있어도 run.ps1이 자동으로 기동한다.

## 확인

```bash
curl -I https://vicikyu.site/Account/Login      # 200 기대
nslookup vicikyu.site 1.1.1.1                    # Cloudflare 프록시 IP
curl -I https://bigsungg.taile671ea.ts.net/      # Funnel 회귀 확인(유지됨)
```

## 주의

- `.cloudflare/`는 gitignore 대상이다. 토큰이 유출되면 Zero Trust 대시보드에서 터널을 삭제하고 재발급한다.
- TLS 모드는 Cloudflare 기본(Flexible 아님: Full 또는 Automatic 권장 — 대시보드 SSL/TLS 설정). Tunnel 사용 시 앱까지는 Cloudflare가 암호화한 연결로 전달된다.
