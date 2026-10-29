# Vicikyu 백업 복원 (Windows) — 백업 폴더(기본: backups/ 최신)를 검증·적용하고 기동까지 자동
# 사용: restore.ps1 [-BackupPath 경로] [-Force]   -Force 시 기존 볼륨 덮어쓰기 확인 생략
param([string]$BackupPath, [switch]$Force)
. "$PSScriptRoot\lib\common.ps1"

if (-not $BackupPath) {
    $latest = Get-ChildItem (Join-Path $RepoRoot 'backups') -Directory -Filter 'vicikyu-*' -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending | Select-Object -First 1
    if (-not $latest) { Die 'backups\ 안에 백업 폴더가 없다. 경로를 인자로 지정하거나 먼저 backup.ps1 을 실행할 것.' }
    $BackupPath = $latest.FullName
}
$BackupPath = (Resolve-Path $BackupPath).Path
$manifestPath = Join-Path $BackupPath 'manifest.json'
if (-not (Test-Path $manifestPath)) { Die "$BackupPath 에 manifest.json 이 없다. 백업 폴더째로 전달했는지 확인할 것." }

Wait-Docker
Push-Location $RepoRoot
try {
    # 1) 무결성 검증 — manifest 체크섬과 실제 파일 대조
    $manifest = Get-Content $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($f in $manifest.files) {
        $p = Join-Path $BackupPath $f.name
        if (-not (Test-Path $p)) { Die "백업 파일 누락: $($f.name)" }
        if ((Get-Sha256 $p) -ne $f.sha256) {
            Die "체크섬 불일치: $($f.name) — 백업이 손상됐다."
        }
    }
    Ok "체크섬 검증 통과 ($($manifest.created_at) 백업)"

    # 2) 시크릿·터널 토큰 배치
    New-Item -ItemType Directory -Path (Join-Path $RepoRoot '.secrets'), (Join-Path $RepoRoot '.cloudflare') -Force | Out-Null
    if (Test-Path (Join-Path $BackupPath 'vicikyu-sa.json')) {
        Copy-Item (Join-Path $BackupPath 'vicikyu-sa.json') $SaPath -Force
        Ok '구글 서비스계정 배치 (.secrets/)'
    }
    if (Test-Path (Join-Path $BackupPath 'tunnel-token.txt')) {
        Copy-Item (Join-Path $BackupPath 'tunnel-token.txt') $TokenPath -Force
        Ok '터널 토큰 배치 (.cloudflare/)'
    }

    # 3) DB 볼륨 교체
    docker volume inspect $VolumeName *> $null
    $volExists = ($LASTEXITCODE -eq 0)
    if ($volExists -and -not $Force) {
        $ans = Read-Host "기존 $VolumeName 볼륨을 백업으로 덮어씁니다. 현재 DB는 사라집니다. 계속? (y/N)"
        if ($ans -notmatch '^[Yy]') { Die '사용자 취소' }
    }
    Info '컨테이너 정지 후 볼륨 교체'
    docker compose -p $Project down | Out-Null
    if ($volExists) { docker volume rm $VolumeName | Out-Null }
    docker volume create $VolumeName | Out-Null
    $rel = $BackupPath.Substring($RepoRoot.Length + 1) -replace '\\', '/'
    if ($BackupPath.StartsWith($RepoRoot)) {
        docker run --rm -v "${VolumeName}:/data" -v "${RepoRoot}:/backup" alpine sh -c "cd /data && tar xf /backup/$rel/vbdata.tar"
    } else {
        docker run --rm -v "${VolumeName}:/data" -v "${BackupPath}:/backup:ro" alpine sh -c "cd /data && tar xf /backup/vbdata.tar"
    }
    if ($LASTEXITCODE -ne 0) { Die '볼륨 복원 실패' }
    Ok 'DB 볼륨 복원'

    # 4) 이미지 — 백업에 있으면 로드, 없으면 재빌드
    if (Test-Path (Join-Path $BackupPath 'vicikyu-web.tar')) {
        docker load -i (Join-Path $BackupPath 'vicikyu-web.tar') | Out-Null
        Ok '이미지 로드'
    } else {
        Info '이미지 재빌드 (수 분 소요)'
        docker compose -p $Project build | Out-Null
        if ($LASTEXITCODE -ne 0) { Die '빌드 실패' }
    }

    # 5) 기동·헬스체크는 run.ps1 이 수행
    & (Join-Path $PSScriptRoot 'run.ps1')
    if (-not $?) { Die '기동 실패 — run.ps1 로그 참고' }

    # 6) 복원 무결성 — 이번 기동에 마이그레이션이 적용됐으면 볼륨이 백업과 다른 것
    $mig = docker compose -p $Project logs --since 10m web 2>$null | Select-String -Pattern 'Applying migration'
    if ($mig) {
        Warn '이번 기동에 마이그레이션 기록이 있다. 볼륨이 정확히 복원됐는지 의심할 것:'
        $mig | Select-Object -First 5 | ForEach-Object { Warn "  $($_.Line)" }
    } else {
        Ok '마이그레이션 적용 없음 — 백업 상태 그대로 기동됨'
    }
    Ok '복원 완료. 기존 계정(새 시드 admin 이 아님)으로 로그인되는지 확인할 것.'
} finally { Pop-Location }
