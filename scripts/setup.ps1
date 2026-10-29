# Vicikyu 실행 환경 설치 (Windows) — Docker Desktop + cloudflared + 필수 파일 검사
# 멱등: 이미 설치된 것은 건너뛴다.
. "$PSScriptRoot\lib\common.ps1"

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
         ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { Warn '관리자 권한이 아니다. 설치 진행 시 UAC 승인 창이 뜰 수 있다.' }

# 1) Docker
if (Test-Docker) {
    Ok 'Docker 실행 중'
} elseif (Get-Command docker -ErrorAction SilentlyContinue) {
    Wait-Docker 180
    Ok 'Docker 데몬 기동'
} else {
    Info 'Docker Desktop 설치 (winget)'
    winget install -e --id Docker.DockerDesktop --accept-package-agreements --accept-source-agreements
    if ($LASTEXITCODE -ne 0) { Die 'winget 설치 실패 — Docker Desktop 수동 설치 후 setup.ps1 재실행' }
    Warn '설치 직후 재부팅(WSL2)이 필요할 수 있다. 재부팅 후 setup.ps1 을 다시 실행하면 이어서 진행된다.'
    Wait-Docker 600
    Ok 'Docker 데몬 기동'
}

# 2) cloudflared
if (Get-Cloudflared) {
    Ok 'cloudflared 설치됨'
} else {
    Info 'cloudflared 설치 (winget)'
    winget install -e --id Cloudflare.cloudflared --accept-package-agreements --accept-source-agreements
    if ($LASTEXITCODE -ne 0) { Die 'winget 설치 실패 — Cloudflare 다운로드 페이지에서 수동 설치: https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/downloads/' }
    if (Get-Cloudflared) { Ok 'cloudflared 설치 완료' }
    else { Warn 'cloudflared 가 현재 터미널 PATH에 없다. 새 터미널에서 run.ps1 실행 시 자동 인식된다.' }
}

# 3) 필수 파일 검사 (발급은 대시보드 작업이라 자동화 불가 — 안내만)
Write-Host ''
Info '필수 파일 검사'
if (Test-Path $SaPath)    { Ok '.secrets/vicikyu-sa.json 있음' }
else { Warn '.secrets/vicikyu-sa.json 없음 — Google 서비스계정 키를 이 위치에 저장할 것 (시트 연결 필수)' }
if (Test-Path $TokenPath) { Ok '.cloudflare/tunnel-token.txt 있음' }
else { Warn '.cloudflare/tunnel-token.txt 없음 — Cloudflare Zero Trust > Networks > Tunnels 에서 토큰을 발급받아 저장할 것 (인터넷 공개용)' }

Write-Host ''
Ok 'setup 완료. 다음: run.ps1 (복구는 restore.ps1)'
