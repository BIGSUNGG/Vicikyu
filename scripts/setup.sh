#!/usr/bin/env bash
# Vicikyu 실행 환경 설치 (Linux) — Docker + cloudflared + 필수 파일 검사
# 멱등: 이미 설치된 것은 건너뛴다. Ubuntu/Debian/Raspberry Pi OS(arm64) 지원.
set -euo pipefail
. "$(dirname "$0")/lib/common.sh"

# 1) Docker
if command -v docker >/dev/null 2>&1; then
	ok 'Docker 설치됨'
else
	info 'Docker 설치 (get.docker.com)'
	curl -fsSL https://get.docker.com | $SUDO sh
	$SUDO usermod -aG docker "${SUDO_USER:-$USER}"
	warn 'docker 그룹 적용을 위해 재로그인(또는 newgrp docker)이 필요할 수 있다.'
fi
if [ -d /run/systemd/system ]; then $SUDO systemctl enable --now docker >/dev/null; fi
wait_docker 180
ok 'Docker 데몬 실행 중'

# 2) cloudflared
if command -v cloudflared >/dev/null 2>&1; then
	ok 'cloudflared 설치됨'
else
	case "$(uname -m)" in
	x86_64) arch=amd64 ;;
	aarch64) arch=arm64 ;;
	*) die "지원하지 않는 아키텍처: $(uname -m)" ;;
	esac
	info "cloudflared 바이너리 설치 (linux-$arch)"
	$SUDO curl -fsSL -o /usr/local/bin/cloudflared \
		"https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-linux-$arch"
	$SUDO chmod +x /usr/local/bin/cloudflared
	ok 'cloudflared 설치 완료 (/usr/local/bin/cloudflared)'
fi
command -v python3 >/dev/null 2>&1 ||
	warn 'python3 없음 — restore.sh 체크섬 검증에 필요하다. 설치 권장.'

# 3) 필수 파일 검사 (발급은 대시보드 작업이라 자동화 불가 — 안내만)
echo
info '필수 파일 검사'
if [ -f "$SA_PATH" ]; then
	ok '.secrets/vicikyu-sa.json 있음'
else warn '.secrets/vicikyu-sa.json 없음 — Google 서비스계정 키를 이 위치에 저장할 것 (시트 연결 필수)'; fi
if [ -f "$TOKEN_PATH" ]; then
	ok '.cloudflare/tunnel-token.txt 있음'
else warn '.cloudflare/tunnel-token.txt 없음 — Cloudflare Zero Trust > Networks > Tunnels 에서 토큰을 발급받아 저장할 것 (인터넷 공개용)'; fi

echo
ok 'setup 완료. 다음: ./scripts/run.sh (복구는 ./scripts/restore.sh)'
