Set-Location $PSScriptRoot

# Docker Desktop 꺼져 있으면 자동 실행 + 기동 대기
docker info *> $null
if ($LASTEXITCODE) {
    Start-Process -FilePath 'C:\Program Files\Docker\Docker\Docker Desktop.exe'
    while ($LASTEXITCODE) { Start-Sleep -Seconds 5; docker info *> $null }
}

# DB 포함 컨테이너 기동 (마이그레이션·시드 자동)
docker compose up -d --build

# Tailscale 인터넷 공개 터널
& 'C:\Program Files\Tailscale\tailscale.exe' funnel --bg 5121

# 브라우저로 로그인 페이지 열기 (admin / Vbc!Docker#2026-admin)
Start-Process -FilePath 'http://localhost:5121/Account/Login'
