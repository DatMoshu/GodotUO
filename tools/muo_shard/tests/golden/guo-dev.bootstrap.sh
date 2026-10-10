#!/usr/bin/env bash
# muo_shard bootstrap for profile guo-dev. Generated: review it, then run it on the host as root.
# It is idempotent and holds no secret; those live in /etc/muo/guo-dev.env.
set -euo pipefail
die() { echo "muo_shard: $*" >&2; exit 1; }
[ "$(id -u)" = 0 ] || die "run this as root"
ID=guo-dev
SVC_USER=muo-guo-dev
BASE=/srv/muo/guo-dev
SRC=$BASE/src
DIST=$BASE/dist
ENV_FILE=/etc/muo/guo-dev.env
UNIT=muo-guo-dev.service
PORT=2593

# packages: only what is missing, so a second run touches nothing
want='dotnet-sdk-10.0 git zstd libdeflate-dev libargon2-1 libargon2-dev ca-certificates python3 iproute2'
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
install -d -o "$SVC_USER" -g "$SVC_USER" -m 0750 /var/backups/muo/guo-dev
install -d -m 0755 /etc/muo

# the host-local values file: created empty and closed, never overwritten
[ -e "$ENV_FILE" ] || install -m 0600 -o root -g root /dev/null "$ENV_FILE"

echo "muo_shard: bootstrap of $ID done. Put MUO_CLIENT_DATA, MUO_ADMIN_USER and MUO_ADMIN_PASSWORD in $ENV_FILE (run.py secrets), then deploy."
