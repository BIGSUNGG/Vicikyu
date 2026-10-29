#!/usr/bin/env bash
# Vicikyu 백업 복원 (Linux) — 백업 폴더(기본: backups/ 최신)를 검증·적용하고 기동까지 자동
# 사용: restore.sh [백업경로] [--force]   --force 시 기존 볼륨 덮어쓰기 확인 생략
set -euo pipefail
. "$(dirname "$0")/lib/common.sh"

FORCE=0
args=()
for a in "$@"; do
	if [ "$a" = "--force" ]; then FORCE=1; else args+=("$a"); fi
done
BACKUP=${args[0]:-}

if [ -z "$BACKUP" ]; then
	BACKUP=$(ls -1d "$REPO_ROOT"/backups/vicikyu-* 2>/dev/null | sort -r | head -n1 || true)
	[ -n "$BACKUP" ] || die 'backups/ 안에 백업 폴더가 없다. 경로를 인자로 지정하거나 먼저 backup.sh 실행.'
fi
[ -f "$BACKUP/manifest.json" ] ||
	die "$BACKUP 에 manifest.json 이 없다. 백업 폴더째로 전달했는지 확인할 것."

wait_docker
cd "$REPO_ROOT"

# 1) 무결성 검증 — manifest 체크섬과 실제 파일 대조
command -v python3 >/dev/null 2>&1 || die 'python3 필요(체크섬 검증용)'
python3 - "$BACKUP" <<'PYEOF' || die '백업 무결성 검증 실패'
import hashlib, json, os, sys
d = sys.argv[1]
m = json.load(open(os.path.join(d, 'manifest.json'), encoding='utf-8'))
for f in m['files']:
    p = os.path.join(d, f['name'])
    if not os.path.isfile(p):
        sys.exit('백업 파일 누락: ' + f['name'])
    h = hashlib.sha256(open(p, 'rb').read()).hexdigest()
    if h != f['sha256']:
        sys.exit('체크섬 불일치: ' + f['name'] + ' — 백업 손상')
print('체크섬 검증 통과 (%s 백업)' % m['created_at'])
PYEOF

# 2) 시크릿·터널 토큰 배치
mkdir -p "$REPO_ROOT/.secrets" "$REPO_ROOT/.cloudflare"
if [ -f "$BACKUP/vicikyu-sa.json" ]; then
	cp -f "$BACKUP/vicikyu-sa.json" "$SA_PATH"
	ok '구글 서비스계정 배치 (.secrets/)'
fi
if [ -f "$BACKUP/tunnel-token.txt" ]; then
	cp -f "$BACKUP/tunnel-token.txt" "$TOKEN_PATH"
	ok '터널 토큰 배치 (.cloudflare/)'
fi

# 3) DB 볼륨 교체
VOL_EXISTS=0
if docker volume inspect "$VOLUME" >/dev/null 2>&1; then VOL_EXISTS=1; fi
if [ "$VOL_EXISTS" = 1 ] && [ "$FORCE" = 0 ]; then
	printf '기존 %s 볼륨을 백업으로 덮어씁니다. 현재 DB는 사라집니다. 계속? (y/N) ' "$VOLUME"
	read -r ans
	case "$ans" in
	y | Y) ;;
	*) die '사용자 취소' ;;
	esac
fi
info '컨테이너 정지 후 볼륨 교체'
docker compose -p "$PROJECT" down >/dev/null
if [ "$VOL_EXISTS" = 1 ]; then docker volume rm "$VOLUME" >/dev/null; fi
docker volume create "$VOLUME" >/dev/null
docker run --rm -v "$VOLUME:/data" -v "$BACKUP:/backup:ro" alpine sh -c 'cd /data && tar xf /backup/vbdata.tar'
ok 'DB 볼륨 복원'

# 4) 이미지 — 백업에 있으면 로드, 없으면 재빌드
if [ -f "$BACKUP/vicikyu-web.tar" ]; then
	docker load -i "$BACKUP/vicikyu-web.tar" >/dev/null
	ok '이미지 로드'
else
	info '이미지 재빌드 (수 분 소요)'
	docker compose -p "$PROJECT" build >/dev/null
fi

# 5) 기동·헬스체크는 run.sh 이 수행
"$(dirname "$0")/run.sh"

# 6) 복원 무결성 — 이번 기동에 마이그레이션이 적용됐으면 볼륨이 백업과 다른 것
if docker compose -p "$PROJECT" logs --since 10m web 2>/dev/null | grep -qi 'Applying migration'; then
	warn '이번 기동에 마이그레이션 기록이 있다. 볼륨이 정확히 복원됐는지 의심할 것.'
else
	ok '마이그레이션 적용 없음 — 백업 상태 그대로 기동됨'
fi
ok '복원 완료. 기존 계정(새 시드 admin 이 아님)으로 로그인되는지 확인할 것.'
