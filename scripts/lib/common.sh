#!/usr/bin/env bash
# Vicikyu 운영 스크립트 공통 함수 (bash 4+)
# 사용: . "$(dirname "$0")/lib/common.sh"
set -u

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PROJECT=vicikyu # compose 프로젝트명 고정 — 폴더명이 달라져도 같은 볼륨을 찾는다
VOLUME=vicikyu_vbdata
IMAGE_REF=vicikyu-web:latest
HEALTH_URL=http://localhost:5122/Account/Login
PUBLIC_URL=https://vicikyu.site/Account/Login
SA_PATH="$REPO_ROOT/.secrets/vicikyu-sa.json"
TOKEN_PATH="$REPO_ROOT/.cloudflare/tunnel-token.txt"
SUDO=""
[ "$(id -u)" -ne 0 ] && SUDO="sudo"

die() {
	echo "[오류] $1" >&2
	exit 1
}
info() { echo "[..] $1"; }
ok() { echo "[완료] $1"; }
warn() { echo "[주의] $1" >&2; }

have_docker() { docker info >/dev/null 2>&1; }

# Docker 데몬 대기 — systemd면 enable+start 시도
wait_docker() {
	local timeout=${1:-180}
	local deadline=$((SECONDS + timeout))
	have_docker && return 0
	if command -v systemctl >/dev/null 2>&1; then
		$SUDO systemctl enable --now docker >/dev/null 2>&1 || true
	fi
	while [ "$SECONDS" -lt "$deadline" ]; do
		sleep 5
		have_docker && return 0
	done
	die "Docker 데몬이 ${timeout}초 안에 준비되지 않았다. setup.sh 실행 후 재시도할 것."
}

# URL이 HTTP 200을 반환할 때까지 대기
wait_http() { # $1=url $2=timeout_sec(기본 300)
	local url=$1
	local deadline=$((SECONDS + ${2:-300}))
	local code
	while [ "$SECONDS" -lt "$deadline" ]; do
		code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 "$url" 2>/dev/null || echo 000)
		[ "$code" = "200" ] && return 0
		sleep 3
	done
	return 1
}

sha256_of() { sha256sum "$1" | awk '{print $1}'; }
