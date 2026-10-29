# Vicikyu 배포·실행·재개 (Windows) — 컨테이너 기동 + Cloudflare 터널 + 헬스체크
# 사용: run.ps1 [-Funnel] [-NoTunnel]   -Funnel: Tailscale funnel 병행, -NoTunnel: 터널 생략(로컬만)
param([switch]$Funnel, [switch]$NoTunnel)
. "$PSScriptRoot\lib\common.ps1"

Wait-Docker
Push-Location $RepoRoot
try {
    docker image inspect $ImageRef *> $null
    if ($LASTEXITCODE -ne 0) { Info '이미지가 없어 빌드한다 (첫 실행은 수 분 소요)' }
    docker compose -p $Project up -d
    if ($LASTEXITCODE -ne 0) { Die '컨테이너 기동 실패' }

    if (-not $NoTunnel) {
        $cf = Get-Cloudflared
        if (-not $cf) {
            Warn 'cloudflared 미설치 — setup.ps1 실행 후 재시도. 지금은 로컬만 공개된다.'
        } elseif (-not (Test-Path $TokenPath)) {
            Warn '.cloudflare\tunnel-token.txt 없음 — Cloudflare 대시보드에서 터널 토큰을 발급받아 저장할 것. 지금은 로컬만 공개된다.'
        } else {
            $token = (Get-Content $TokenPath -Raw).Trim()
            $svc = Get-Service -Name cloudflared -ErrorAction SilentlyContinue
            if (-not $svc) {
                Info 'cloudflared 서비스 등록 (관리자 권한 필요)'
                & $cf service install $token
                if ($LASTEXITCODE -ne 0) { Die '터널 서비스 등록 실패 — 관리자 권한 PowerShell에서 재실행할 것.' }
            } elseif ($svc.Status -ne 'Running') {
                Start-Service cloudflared
            }
            Info '터널 연결 확인 (vicikyu.site)'
            if (Wait-Http $PublicUrl 90) { Ok "공개 주소 응답: $PublicUrl" }
            else { Warn '공개 주소가 90초 내 응답하지 않았다. Get-Service cloudflared 상태를 확인할 것.' }
        }
    }

    if ($Funnel) {
        if (Get-Command tailscale -ErrorAction SilentlyContinue) {
            & tailscale funnel --bg 5122
            if ($LASTEXITCODE -ne 0) { Warn 'tailscale funnel 실패' }
        } else { Warn 'tailscale 미설치 — funnel 생략' }
    }

    if (Wait-Http $HealthUrl 300) { Ok "로컬 응답: $HealthUrl" }
    else { Die '웹이 300초 내 응답하지 않았다. docker compose -p vicikyu logs web 으로 원인을 확인할 것.' }
    Ok '서비스 실행 중.'
} finally { Pop-Location }
