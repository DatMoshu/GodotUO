#!/usr/bin/env bash
# muo_shard reset for profile guo-dev. Generated: review it, then run it on the host as root.
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

NAME=seed
BACKUP_ROOT=/var/backups/muo/guo-dev
INCLUDE=(Saves)
snap="$BACKUP_ROOT/$NAME.tar.zst"
have_systemd() { [ -d /run/systemd/system ]; }
[ -d "$BASE" ] || die "run bootstrap first"
[ -f "$snap" ] || die "no snapshot $NAME in $BACKUP_ROOT"

# the shard must be stopped while its saves are replaced
was_active=0
if have_systemd; then
    if systemctl is-active --quiet "$UNIT"; then was_active=1; systemctl stop "$UNIT"; fi
elif ss -ltn "sport = :$PORT" | grep -q LISTEN; then
    die "something is listening on tcp/$PORT; stop the shard first"
fi

# what is there now is kept beside it as <folder>.pre-restore (one generation)
for d in "${INCLUDE[@]}"; do
    if [ -e "$DIST/$d" ]; then
        rm -rf -- "$DIST/$d.pre-restore"
        mv -- "$DIST/$d" "$DIST/$d.pre-restore"
    fi
done
tar -C "$DIST" --zstd -xf "$snap"
for d in "${INCLUDE[@]}"; do chown -R "$SVC_USER:$SVC_USER" "$DIST/$d"; done

if [ "$was_active" = 1 ]; then systemctl start "$UNIT"; fi
echo "muo_shard: reset from $NAME done."
