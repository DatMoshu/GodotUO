#!/usr/bin/env bash
# muo_shard admin for profile guo-dev. Generated: review it, then run it on the host as root.
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

# the owner account is made by the shard itself at start, from the two host values
[ -d "$BASE" ] && [ -f "$ENV_FILE" ] || die "run bootstrap first"
set -a; . "$ENV_FILE"; set +a
: "${MUO_ADMIN_USER:?MUO_ADMIN_USER is not set in $ENV_FILE (run.py secrets)}"
: "${MUO_ADMIN_PASSWORD:?MUO_ADMIN_PASSWORD is not set in $ENV_FILE (run.py secrets)}"

if [ -d /run/systemd/system ]; then
    systemctl restart "$UNIT"
    for _ in $(seq 1 60); do
        ss -ltn "sport = :$PORT" | grep -q LISTEN && break
        sleep 1
    done
    ss -ltn "sport = :$PORT" | grep -q LISTEN || die "the shard is not listening on tcp/$PORT; see journalctl -u $UNIT"
else
    echo "muo_shard: no systemd here (a container?). Start the shard with:"
    echo "  set -a; . $ENV_FILE; set +a; runuser -u $SVC_USER -- $DIST/muo-run.sh"
fi
echo "muo_shard: the owner account '$MUO_ADMIN_USER' exists once the shard is up (it is saved at the next autosave)."
