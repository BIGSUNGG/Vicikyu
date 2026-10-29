Set-Location $PSScriptRoot\..
Set-StrictMode -Version Latest

# 로컬 개발 DB 초기화 — VolleyballClub.Web/VolleyballClub.db(+WAL/SHM)를 삭제하고 스키마만 재생성한다.
# 시드(관리자 admin / Seed 비밀번호)는 다음 기동 때 DbInitializer가 자동 적용한다(자동 마이그레이션+시드).
# 운영(Docker /data 볼륨)에는 영향 없다. 리포지토리 루트의 0바이트 VolleyballClub.db는
# 과거 오기동 흔적(참조 없음)이므로 대상에서 제외한다.

$targets = @(
    'VolleyballClub.Web/VolleyballClub.db'
    'VolleyballClub.Web/VolleyballClub.db-wal'
    'VolleyballClub.Web/VolleyballClub.db-shm'
)

# (1) 실행 중인 개발 서버가 DB를 잠그고 있으면 중단 — 서버는 직접 종료한다(자동 kill하지 않는다)
foreach ($t in ($targets | Where-Object { Test-Path $_ })) {
    try {
        [IO.File]::Open((Resolve-Path $t), 'Open', 'ReadWrite', 'None').Close()
    }
    catch {
        Write-Error "DB 파일이 잠겨 있다 — 개발 서버(dotnet run)가 실행 중인 것 같다. 서버를 종료한 뒤 다시 실행할 것. 대상: $t"
        exit 1
    }
}

# (2) 삭제 대상을 먼저 출력하고 삭제한다
$existing = $targets | Where-Object { Test-Path $_ }
if (-not $existing) {
    Write-Host "삭제할 DB 파일이 없다 — 스키마만 새로 생성한다."
}
foreach ($t in $existing) {
    Write-Host ("삭제: {0} ({1:N0} bytes)" -f $t, (Get-Item $t).Length)
    Remove-Item $t
}

# (3) 스키마 즉시 재생성(마이그레이션만 적용 — 시드는 앱 기동 시)
dotnet ef database update --project VolleyballClub.Infrastructure --startup-project VolleyballClub.Web
if ($LASTEXITCODE) {
    Write-Error "마이그레이션이 실패했다 — 위의 dotnet ef 출력을 확인할 것."
    exit 1
}

Write-Host ""
Write-Host "로컬 개발 DB 초기화 완료."
Write-Host "시드(관리자 admin / Seed 비밀번호)는 다음 기동 때 자동 적용된다: dotnet run --project VolleyballClub.Web"
Write-Host "기동에는 Redis(6379)가 필요하다 — scripts/run.ps1 또는 docker로 띄워 둘 것."
