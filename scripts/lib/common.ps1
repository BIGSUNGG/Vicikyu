# Vicikyu 운영 스크립트 공통 함수 (Windows PowerShell 5.1+)
# 사용: . "$PSScriptRoot\lib\common.ps1"

$ErrorActionPreference = 'Stop'

$RepoRoot   = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$Project    = 'vicikyu'          # compose 프로젝트명 고정 — 폴더명이 달라져도 같은 볼륨을 찾는다
$VolumeName = 'vicikyu_vbdata'
$ImageRef   = 'vicikyu-web:latest'
$HealthUrl  = 'http://localhost:5122/Account/Login'
$PublicUrl  = 'https://vicikyu.site/Account/Login'
$SaPath     = Join-Path $RepoRoot '.secrets\vicikyu-sa.json'
$TokenPath  = Join-Path $RepoRoot '.cloudflare\tunnel-token.txt'

function Die([string]$Message) { Write-Host "[오류] $Message" -ForegroundColor Red; exit 1 }
function Info([string]$Message) { Write-Host "[..] $Message" }
function Ok([string]$Message)   { Write-Host "[완료] $Message" -ForegroundColor Green }
function Warn([string]$Message) { Write-Host "[주의] $Message" -ForegroundColor Yellow }

function Test-Docker {
    docker info *> $null
    return ($LASTEXITCODE -eq 0)
}

# Docker 데몬 대기 — 꺼져 있으면 Docker Desktop을 실행한다
function Wait-Docker([int]$TimeoutSec = 180) {
    if (Test-Docker) { return }
    $desktop = 'C:\Program Files\Docker\Docker\Docker Desktop.exe'
    if (Test-Path $desktop) {
        Info 'Docker Desktop 기동 중...'
        Start-Process -FilePath $desktop | Out-Null
    }
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 5
        if (Test-Docker) { return }
    }
    Die "Docker 데몬이 ${TimeoutSec}초 안에 준비되지 않았다. Docker 설치/실행 후 재시도할 것."
}

# URL이 HTTP 200을 반환할 때까지 대기
function Wait-Http([string]$Url, [int]$TimeoutSec = 300) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        try {
            $r = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 5
            if ($r.StatusCode -eq 200) { return $true }
        } catch { }
        Start-Sleep -Seconds 3
    }
    return $false
}

# cloudflared 실행 파일 경로 — PATH 우선, winget/MSI 표준 경로 차선
function Get-Cloudflared {
    $cmd = Get-Command cloudflared -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $candidates = @(
        "${env:ProgramFiles(x86)}\cloudflared\cloudflared.exe",
        "$env:ProgramFiles\cloudflared\cloudflared.exe",
        "$env:LOCALAPPDATA\Microsoft\WinGet\Links\cloudflared.exe"
    )
    foreach ($p in $candidates) { if ($p -and (Test-Path $p)) { return $p } }
    return $null
}

# 파일 SHA256 — PS7 의 PSModulePath 가 혼재된 환경에서는 Get-FileHash 해석이 깨질 수 있어 certutil 로 대체한다
function Get-Sha256([string]$Path) {
    try { return (Get-FileHash $Path -Algorithm SHA256 -ErrorAction Stop).Hash.ToLower() }
    catch { }
    return ((certutil -hashfile $Path SHA256)[1] -replace '\s', '').ToLower()
}
