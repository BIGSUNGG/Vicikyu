# Vicikyu 전체 상태 백업 (Windows) — DB 볼륨 + 구글 시크릿 + Cloudflare 터널 토큰 + manifest
# 사용: backup.ps1 [-Image]   -Image 시 docker save 이미지 tar 추가
param([switch]$Image)
. "$PSScriptRoot\lib\common.ps1"

Wait-Docker
Push-Location $RepoRoot
try {
    docker volume inspect $VolumeName *> $null
    if ($LASTEXITCODE -ne 0) { Die "볼륨 $VolumeName 이 없다. 먼저 run.ps1 로 서비스를 한 번 이상 기동했는지 확인할 것." }

    $haveSa    = Test-Path $SaPath
    $haveToken = Test-Path $TokenPath
    if (-not ($haveSa -and $haveToken)) {
        Warn '시크릿/터널 토큰 파일이 일부 없다 — 있는 것만 백업된다.'
        Warn ("  구글 SA: {0} / 터널 토큰: {1}" -f $haveSa, $haveToken)
    }

    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $dest  = Join-Path $RepoRoot "backups\vicikyu-$stamp"
    New-Item -ItemType Directory -Path $dest -Force | Out-Null

    $wasRunning = [bool](docker compose -p $Project ps -q web)
    if ($wasRunning) {
        Info 'web 컨테이너 정지 (SQLite WAL 무결성 — 백업 후 자동 재개)'
        docker compose -p $Project stop web
        if ($LASTEXITCODE -ne 0) { Die 'web 정지 실패' }
    } else {
        Info 'web 컨테이너가 이미 정지 상태 — 그대로 백업'
    }

    try {
        Info 'DB 볼륨 tar 추출'
        docker run --rm -v "${VolumeName}:/data" -v "${RepoRoot}:/backup" alpine `
            tar cf "/backup/backups/vicikyu-$stamp/vbdata.tar" -C /data .
        if ($LASTEXITCODE -ne 0) { Die '볼륨 백업 실패' }

        if ($haveSa)    { Copy-Item $SaPath    (Join-Path $dest 'vicikyu-sa.json') }
        if ($haveToken) { Copy-Item $TokenPath (Join-Path $dest 'tunnel-token.txt') }

        if ($Image) {
            Info '이미지 tar 추출 (docker save)'
            docker save -o (Join-Path $dest 'vicikyu-web.tar') $ImageRef
            if ($LASTEXITCODE -ne 0) { Die '이미지 저장 실패' }
        }

        $gitRev = ''
        try { $gitRev = (git rev-parse --short HEAD 2>$null) } catch { $gitRev = '' }
        if (-not $gitRev) { $gitRev = 'unknown' }
        $sheetId = ''
        $m = Select-String -Path (Join-Path $RepoRoot 'docker-compose.yml') -Pattern 'Google__SpreadsheetId:\s*(\S+)'
        if ($m) { $sheetId = $m.Matches[0].Groups[1].Value }

        $files = Get-ChildItem $dest -File | ForEach-Object {
            @{ name = $_.Name; sha256 = (Get-Sha256 $_.FullName) }
        }
        [ordered]@{
            created_at     = (Get-Date).ToString('o')
            git_commit     = $gitRev
            spreadsheet_id = $sheetId
            files          = $files
        } | ConvertTo-Json -Depth 4 | Set-Content -Path (Join-Path $dest 'manifest.json') -Encoding UTF8
    } finally {
        if ($wasRunning) {
            Info 'web 컨테이너 재기동'
            docker compose -p $Project start web | Out-Null
        }
    }

    Ok "백업 완료: $dest"
    Warn '이 폴더에는 DB·구글 시크릿·터널 토큰 전부가 들어 있다. 보관·전송에 주의하고 git에 커밋하지 않는다.'
} finally { Pop-Location }
