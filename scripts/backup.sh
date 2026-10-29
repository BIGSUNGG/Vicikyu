#!/usr/bin/env bash
# Vicikyu 전체 상태 백업 (Linux) — DB 볼륨 + 구글 시크릿 + Cloudflare 터널 토큰 + manifest
# 사용: backup.sh [--image]   --image 시 docker save 이미지 tar 추가
set -euo pipefail
. "$(dirname "$0")/lib/common.sh"

IMAGE=0
if [ "${1:-}" = "--image" ]; then IMAGE=1; fi

wait_docker
docker volume inspect "$VOLUME" >/dev/null 2>&1 \
    || die "볼륨 $VOLUME 이 없다. 먼저 run.sh 로 서비스를 한 번 이상 기동했는지 확인할 것."

HAVE_SA=0; [ -f "$SA_PATH" ] && HAVE_SA=1 || true
HAVE_TK=0; [ -f "$TOKEN_PATH" ] && HAVE_TK=1 || true
if [ "$HAVE_SA" != 1 ] || [ "$HAVE_TK" != 1 ]; then
    warn '시크릿/터널 토큰 파일이 일부 없다 — 있는 것만 백업된다.'
fi

STAMP=$(date +%Y%m%d-%H%M%S)
DEST="$REPO_ROOT/backups/vicikyu-$STAMP"
REL="backups/vicikyu-$STAMP"
mkdir -p "$DEST"

WAS_RUNNING=0
if [ -n "$(docker compose -p "$PROJECT" ps -q web)" ]; then WAS_RUNNING=1; fi
if [ "$WAS_RUNNING" = 1 ]; then
    info 'web 컨테이너 정지 (SQLite WAL 무결성 — 백업 후 자동 재개)'
    docker compose -p "$PROJECT" stop web
else
    info 'web 컨테이너가 이미 정지 상태 — 그대로 백업'
fi

restore_web() {
    if [ "$WAS_RUNNING" = 1 ]; then
        info 'web 컨테이너 재기동'
        docker compose -p "$PROJECT" start web >/dev/null
    fi
}
trap restore_web EXIT

info 'DB 볼륨 tar 추출'
docker run --rm -v "$VOLUME:/data" -v "$REPO_ROOT:/backup" alpine \
    tar cf "/backup/$REL/vbdata.tar" -C /data .

if [ "$HAVE_SA" = 1 ]; then cp "$SA_PATH" "$DEST/vicikyu-sa.json"; fi
if [ "$HAVE_TK" = 1 ]; then cp "$TOKEN_PATH" "$DEST/tunnel-token.txt"; fi

if [ "$IMAGE" = 1 ]; then
    info '이미지 tar 추출 (docker save)'
    docker save -o "$DEST/vicikyu-web.tar" "$IMAGE_REF"
fi

GIT_REV=$(git -C "$REPO_ROOT" rev-parse --short HEAD 2>/dev/null || echo unknown)
SHEET_ID=$(grep -oP 'Google__SpreadsheetId:\s*\K\S+' "$REPO_ROOT/docker-compose.yml" || true)

{
    printf '{\n  "created_at": "%s",\n  "git_commit": "%s",\n  "spreadsheet_id": "%s",\n  "files": [\n' \
        "$(date -Is)" "$GIT_REV" "$SHEET_ID"
    first=1
    for f in "$DEST"/*; do
        b=$(basename "$f")
        if [ "$b" = "manifest.json" ]; then continue; fi
        if [ "$first" = 1 ]; then first=0; else printf ',\n'; fi
        printf '    {"name": "%s", "sha256": "%s"}' "$b" "$(sha256_of "$f")"
    done
    printf '\n  ]\n}\n'
} > "$DEST/manifest.json"

ok "백업 완료: $DEST"
warn '이 폴더에는 DB·구글 시크릿·터널 토큰 전부가 들어 있다. 보관·전송에 주의하고 git에 커밋하지 않는다.'
