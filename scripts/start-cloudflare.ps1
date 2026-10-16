Set-Location $PSScriptRoot\..

# Cloudflare Tunnel 서비스 기동 (vicikyu.site 실서비스 경로)
# 토큰 파일: .cloudflare\tunnel-token.txt (gitignore — 커밋 금지)
#  - 최초 1회: Cloudflare 대시보드(Zero Trust → Networks → Tunnels)에서 터널 생성 후
#    토큰 문자열만 이 파일에 저장하면 이후 자동으로 Windows 서비스로 등록·기동된다.
# 터널 중지: Stop-Service cloudflared

$tokenFile = Join-Path $PSScriptRoot '..\.cloudflare\tunnel-token.txt'

if (-not (Test-Path $tokenFile)) {
    Write-Error ".cloudflare\tunnel-token.txt 파일이 없다. Cloudflare 대시보드에서 터널 토큰을 발급해 저장할 것."
    exit 1
}
$token = (Get-Content $tokenFile -Raw).Trim()

# Docker Desktop 꺼져 있으면 자동 실행 + 기동 대기 (web이 tunnel의 대상)
docker info *> $null
if ($LASTEXITCODE) {
    Start-Process -FilePath 'C:\Program Files\Docker\Docker\Docker Desktop.exe'
    while ($LASTEXITCODE) { Start-Sleep -Seconds 5; docker info *> $null }
}
docker compose up -d

# cloudflared Windows 서비스 등록(최초 1회) 또는 기동
$svc = Get-Service -Name 'cloudflared' -ErrorAction SilentlyContinue
if (-not $svc) {
    & "$env:ProgramFiles\cloudflared\cloudflared.exe" service install $token
} else {
    Start-Service cloudflared
}

Start-Sleep -Seconds 5
Get-Service cloudflared | Format-Table Name, Status
& "$env:ProgramFiles\cloudflared\cloudflared.exe" tunnel info 2>$null

# 브라우저로 로그인 페이지 열기
Start-Process -FilePath 'https://vicikyu.site/Account/Login'
