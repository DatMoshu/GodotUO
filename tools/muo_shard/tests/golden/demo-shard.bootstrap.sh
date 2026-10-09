#!/usr/bin/env bash
# muo_shard bootstrap for profile demo-shard. Generated: review it, then run it on the host as root.
# It is idempotent and holds no secret; those live in /etc/muo/demo-shard.env.
set -euo pipefail
die() { echo "muo_shard: $*" >&2; exit 1; }
[ "$(id -u)" = 0 ] || die "run this as root"
ID=demo-shard
SVC_USER=demo-svc
BASE=/srv/muo/demo-shard
SRC=$BASE/src
DIST=$BASE/dist
ENV_FILE=/etc/muo/demo-shard.env
UNIT=muo-demo-shard.service
PORT=2610

# packages: only what is missing, so a second run touches nothing
want='dotnet-sdk-10.0 git zstd libdeflate-dev libargon2-1 libargon2-dev ca-certificates python3 iproute2 rsync libicu-dev'
missing=
for pkg in $want; do dpkg -s "$pkg" >/dev/null 2>&1 || missing="$missing $pkg"; done
if [ -n "$missing" ]; then
    export DEBIAN_FRONTEND=noninteractive
    apt-get update -qq
    apt-get install -y --no-install-recommends $missing
fi

# service user: a system account with no login shell
id -u "$SVC_USER" >/dev/null 2>&1 || useradd --system --user-group --home-dir "$BASE" --shell /usr/sbin/nologin "$SVC_USER"

# folders
install -d -o "$SVC_USER" -g "$SVC_USER" -m 0750 "$BASE" "$SRC" "$DIST" "$BASE/uodata"
install -d -o "$SVC_USER" -g "$SVC_USER" -m 0750 /var/backups/demo
install -d -m 0755 /etc/muo

# the host-local values file: created empty and closed, never overwritten
[ -e "$ENV_FILE" ] || install -m 0600 -o root -g root /dev/null "$ENV_FILE"

echo "muo_shard: bootstrap of $ID done. Put DEMO_DATA, DEMO_ADMIN and DEMO_ADMIN_PW in $ENV_FILE (run.py secrets), then deploy."
