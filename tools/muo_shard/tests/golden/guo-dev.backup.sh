#!/usr/bin/env bash
# muo_shard backup for profile guo-dev. Generated: review it, then run it on the host as root.
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

[ -d "$BASE" ] || die "run bootstrap first"

# the helper, also what the timer runs
install -d "$(dirname $BASE/muo-backup.sh)"
cat > $BASE/muo-backup.sh <<'MUO_EOF_0'
#!/usr/bin/env bash
# muo_shard backup helper for profile guo-dev (generated). Usage: muo-backup.sh [NAME]
set -euo pipefail
die() { echo "muo_shard: $*" >&2; exit 1; }
DIST=/srv/muo/guo-dev/dist
BACKUP_ROOT=/var/backups/muo/guo-dev
KEEP=14
INCLUDE=(Saves)

name="${1:-$(date -u +%Y%m%d_%H%M%S)}"
[[ "$name" =~ ^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$ ]] || die "bad snapshot name: $name"
dest="$BACKUP_ROOT/$name.tar.zst"
[ ! -e "$dest" ] || die "snapshot $name already exists"
install -d -m 0750 "$BACKUP_ROOT"

# the shard keeps running: it saves on its own schedule and publishes a save by swapping the folder,
# so copy it and keep the copy only if the folder was not swapped or written meanwhile
sig() { local d; for d in "${INCLUDE[@]}"; do stat -c '%i %Y' "$DIST/$d"; done; }
trap 'rm -f -- "$dest.part"' EXIT
ok=0
for _ in 1 2 3; do
    before="$(sig)"
    tar -C "$DIST" --zstd -cf "$dest.part" -- "${INCLUDE[@]}"
    if [ "$(sig)" = "$before" ]; then ok=1; break; fi
    sleep 2
done
[ "$ok" = 1 ] || die "the saves changed during every attempt; run the backup again"
mv -- "$dest.part" "$dest"

# keep the newest $KEEP timestamped snapshots; named ones (a seed) are never pruned
stamped="$(ls -1 "$BACKUP_ROOT" | grep -E '^[0-9]{8}_[0-9]{6}\.tar\.zst$' || true)"
printf '%s\n' "$stamped" | sort -r | tail -n +"$((KEEP + 1))" | while read -r old; do
    if [ -n "$old" ]; then rm -f -- "$BACKUP_ROOT/$old"; fi
done
echo "muo_shard: snapshot $name written to $BACKUP_ROOT"
MUO_EOF_0
chmod 0755 $BASE/muo-backup.sh

# the schedule (systemd hosts only)
if [ -d /run/systemd/system ]; then
install -d "$(dirname /etc/systemd/system/muo-guo-dev-backup.service)"
cat > /etc/systemd/system/muo-guo-dev-backup.service <<'MUO_EOF_0'
[Unit]
Description=Snapshot of the ModernUO shard guo-dev

[Service]
Type=oneshot
ExecStart=/srv/muo/guo-dev/muo-backup.sh
MUO_EOF_0
chmod 0644 /etc/systemd/system/muo-guo-dev-backup.service
install -d "$(dirname /etc/systemd/system/muo-guo-dev-backup.timer)"
cat > /etc/systemd/system/muo-guo-dev-backup.timer <<'MUO_EOF_0'
[Unit]
Description=Scheduled snapshot of the ModernUO shard guo-dev

[Timer]
OnCalendar=*-*-* 04:00:00
Persistent=true

[Install]
WantedBy=timers.target
MUO_EOF_0
chmod 0644 /etc/systemd/system/muo-guo-dev-backup.timer
    systemctl daemon-reload
    systemctl enable --now muo-guo-dev-backup.timer
fi

"$BASE/muo-backup.sh"
