#!/usr/bin/env bash
# muo_shard status for profile demo-shard. Generated: review it, then run it on the host as root.
# It is idempotent and holds no secret; those live in /etc/muo/demo-shard.env.
set -euo pipefail
die() { echo "muo_shard: $*" >&2; exit 1; }
ID=demo-shard
SVC_USER=demo-svc
BASE=/srv/muo/demo-shard
SRC=$BASE/src
DIST=$BASE/dist
ENV_FILE=/etc/muo/demo-shard.env
UNIT=muo-demo-shard.service
PORT=2610

# read-only: changes nothing on the host
if [ -d /run/systemd/system ]; then
    systemctl status "$UNIT" --no-pager || true
else
    echo "no systemd on this host; unit state not available"
fi
if ss -ltn "sport = :$PORT" | grep -q LISTEN; then
    echo "listening: tcp/$PORT"
else
    echo "NOT listening: tcp/$PORT" >&2
    exit 1
fi
