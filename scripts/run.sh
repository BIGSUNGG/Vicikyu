#!/usr/bin/env bash
# Vicikyu 배포·실행·재개 (Linux) — 컨테이너 기동 + Cloudflare 터널 + 헬스체크
# 사용: run.sh [--funnel] [--no-tunnel]   --funnel: Tailscale funnel 병행, --no-tunnel: 터널 생략(로컬만)
set -euo pipefail
. "$(dirname "$0")/lib/common.sh"

FUNNEL=0
NOTUNNEL=0
for a in "$@"; do
	case "$a" in
	--funnel) FUNNEL=1 ;;
	--no-tunnel) NOTUNNEL=1 ;;
	esac
done

wait_docker
cd "$REPO_ROOT"

if ! docker image inspect "$IMAGE_REF" >/dev/null 2>&1; then
	info '이미지가 없어 빌드한다 (첫 실행은 수 분 소요)'
fi
docker compose -p "$PROJECT" up -d

if [ "$NOTUNNEL" = 0 ]; then
	CF=$(command -v cloudflared || true)
	if [ -z "$CF" ]; then
		warn 'cloudflared 미설치 — setup.sh 실행 후 재시도. 지금은 로컬만 공개된다.'
	elif [ ! -f "$TOKEN_PATH" ]; then
		warn '.cloudflare/tunnel-token.txt 없음 — Cloudflare 대시보드에서 터널 토큰을 발급받아 저장할 것. 지금은 로컬만 공개된다.'
	else
		TOKEN=$(tr -d ' \t\r\n' <"$TOKEN_PATH")
		if [ -d /run/systemd/system ]; then
			if $SUDO systemctl is-enabled --quiet cloudflared 2>/dev/null; then
				$SUDO systemctl start cloudflared
			else
				info 'cloudflared 서비스 등록'
				$SUDO "$CF" service install "$TOKEN"
			fi
		else
			# systemd 없는 환경: 백그라운드 실행
			nohup "$CF" tunnel run --token "$TOKEN" >/dev/null 2>&1 &
			warn 'systemd 가 없어 nohup 백그라운드로 터널을 실행했다.'
		fi
		info '터널 연결 확인 (vicikyu.site)'
		if wait_http "$PUBLIC_URL" 90; then
			ok "공개 주소 응답: $PUBLIC_URL"
		else warn '공개 주소가 90초 내 응답하지 않았다. systemctl status cloudflared 를 확인할 것.'; fi
	fi
fi

if [ "$FUNNEL" = 1 ]; then
	if command -v tailscale >/dev/null 2>&1; then
		tailscale funnel --bg 5122 || warn 'tailscale funnel 실패'
	else
		warn 'tailscale 미설치 — funnel 생략'
	fi
fi

if wait_http "$HEALTH_URL" 300; then
	ok "로컬 응답: $HEALTH_URL"
else die '웹이 300초 내 응답하지 않았다. docker compose -p vicikyu logs web 으로 원인을 확인할 것.'; fi
ok '서비스 실행 중.'
