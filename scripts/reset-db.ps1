Set-Location $PSScriptRoot\..
Set-StrictMode -Version Latest

# 로컬 개발 DB 초기화 — 개발 서버(5121 리스너)를 자동 종료하고 VolleyballClub.Web/VolleyballClub.db(+WAL/SHM)를 삭제해
# 스키마만 재생성한다.
# 시드(관리자 admin / Seed 비밀번호)는 다음 기동 때 DbInitializer가 자동 적용한다(자동 마이그레이션+시드).
# 운영(Docker /data 볼륨)에는 영향 없다. 리포지토리 루트의 0바이트 VolleyballClub.db는
# 과거 오기동 흔적(참조 없음)이므로 대상에서 제외한다.

$targets = @(
    'VolleyballClub.Web/VolleyballClub.db'
    'VolleyballClub.Web/VolleyballClub.db-wal'
    'VolleyballClub.Web/VolleyballClub.db-shm'
)

# (1) 개발 서버(5121 리스너)가 실행 중이면 자동 종료 — DB·DLL 잠금이 리셋과 ef 빌드를 막기 때문이다.
#     강제 종료인 이유: 리셋은 어차피 DB를 지우므로 종료 시 백업 훅이 무의미하고, SQLite WAL은 비정상 종료에도 일관성이 유지된다.
$conn = Get-NetTCPConnection -LocalPort 5121 -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
if ($conn) {
    $srvPid = $conn.OwningProcess
    Write-Host "개발 서버 자동 종료: PID $srvPid (포트 5121)"
    taskkill /PID $srvPid /T /F | Out-Null
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-NetTCPConnection -LocalPort 5121 -State Listen -ErrorAction SilentlyContinue) -and ((Get-Date) -lt $deadline)) {
        Start-Sleep -Milliseconds 300
    }
}

# (2) 잠금 재확인 — 자동 종료 후에도 잠겨 있으면 다른 프로세스이므로 중단한다
foreach ($t in ($targets | Where-Object { Test-Path $_ })) {
    try {
        [IO.File]::Open((Resolve-Path $t), 'Open', 'ReadWrite', 'None').Close()
    }
    catch {
        Write-Error "자동 종료 후에도 DB 파일이 잠겨 있다 — 다른 프로세스가 잠그고 있으니 확인할 것. 대상: $t"
        exit 1
    }
}

# (3) 삭제 대상을 먼저 출력하고 삭제한다
$existing = $targets | Where-Object { Test-Path $_ }
if (-not $existing) {
    Write-Host "삭제할 DB 파일이 없다 — 스키마만 새로 생성한다."
}
foreach ($t in $existing) {
    Write-Host ("삭제: {0} ({1:N0} bytes)" -f $t, (Get-Item $t).Length)
    Remove-Item $t
}

# (4) 스키마 즉시 재생성(마이그레이션만 적용 — 시드는 앱 기동 시)
dotnet ef database update --project VolleyballClub.Infrastructure --startup-project VolleyballClub.Web
if ($LASTEXITCODE) {
    Write-Error "마이그레이션이 실패했다 — 위의 dotnet ef 출력을 확인할 것."
    exit 1
}

Write-Host ""
Write-Host "로컬 개발 DB 초기화 완료."
Write-Host "시드(관리자 admin / Seed 비밀번호)는 다음 기동 때 자동 적용된다: dotnet run --project VolleyballClub.Web"
