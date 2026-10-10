#!/usr/bin/env bash
# muo_shard deploy for profile demo-shard. Generated: review it, then run it on the host as root.
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

PIN=0123456789abcdef0123456789abcdef01234567
CLONE_URL=https://example.invalid/ModernUO.git

as_user() { runuser -u "$SVC_USER" -- env HOME="$BASE" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 "$@"; }
have_systemd() { [ -d /run/systemd/system ]; }

# preflight: bootstrap ran, the host values are there
[ -d "$BASE" ] && [ -f "$ENV_FILE" ] || die "run bootstrap first"
set -a; . "$ENV_FILE"; set +a
: "${DEMO_DATA:?DEMO_DATA is not set in $ENV_FILE}"
[ -d "$DEMO_DATA" ] || die "DEMO_DATA is not a folder on this host"
: "${MUO_REPO:?MUO_REPO is not set in $ENV_FILE (a checkout of the profile repo, for content)}"

# stop the shard before its files change
if have_systemd && systemctl is-active --quiet "$UNIT"; then systemctl stop "$UNIT"; fi

# patches (embedded)

# checkout at the pin; patches off before the pin moves, on after
if [ ! -d "$SRC/.git" ]; then as_user git clone --no-checkout "$CLONE_URL" "$SRC"; fi
as_user git -C "$SRC" cat-file -e "$PIN^{commit}" 2>/dev/null || as_user git -C "$SRC" fetch origin
# global.json is rewritten below for the installed SDK; put it back before anything else looks at the tree
as_user git -C "$SRC" update-index --no-skip-worktree global.json 2>/dev/null || true
as_user git -C "$SRC" checkout -q -- global.json 2>/dev/null || true
head="$(as_user git -C "$SRC" rev-parse -q --verify HEAD 2>/dev/null || true)"
if [ "$head" != "$PIN" ]; then
    as_user git -C "$SRC" checkout -q --detach "$PIN"
fi

# the archive's SDK can trail the SDK version the pin's global.json names; build with the installed one
if [ -f "$SRC/global.json" ]; then
    sdk="$(as_user sh -c 'cd / && dotnet --version')"
    pinned="$(sed -n -E 's/.*"version": *"([^"]*)".*/\1/p' "$SRC/global.json" | head -n 1)"
    if [ "$pinned" != "$sdk" ]; then echo "muo_shard: warning: global.json names SDK $pinned, building with the installed SDK $sdk" >&2; fi
    as_user sed -i -E "s/\"version\": *\"[^\"]*\"/\"version\": \"$sdk\"/" "$SRC/global.json"
    as_user git -C "$SRC" update-index --skip-worktree global.json
fi

# build
arch=x64; [ "$(uname -m)" = aarch64 ] && arch=arm64
as_user sh -c 'cd "$1" && exec bash ./publish.sh release linux "$2"' sh "$SRC" "$arch"
OUT="$SRC/Distribution"
[ -x "$OUT/ModernUO" ] || die "the build left no ModernUO in $OUT"
as_user cp -a "$OUT/." "$DIST/"

# extra content assemblies
install -d "$(dirname $BASE/muo-assemblies.py)"
cat > $BASE/muo-assemblies.py <<'MUO_EOF_0'
import json, os, sys
path, name = sys.argv[1:3]
items = json.load(open(path, encoding="utf-8")) if os.path.exists(path) else []
if name not in items:
    items.append(name)
with open(path, "w", encoding="utf-8", newline="\n") as f:
    json.dump(items, f, indent=2)
    f.write("\n")
MUO_EOF_0
chmod 0644 $BASE/muo-assemblies.py
install -d -o "$SVC_USER" -g "$SVC_USER" "$DIST/Assemblies" "$DIST/Data"
as_user sh -c 'cd "$1" && exec dotnet publish "$2" -c Release -o "$3"' sh "$SRC" tools/muo_shard/tests/fixtures/server/Scripts/Demo.csproj "$BASE/asm"
as_user cp -f "$BASE/asm/"CustomContent.dll "$DIST/Assemblies/"
python3 "$BASE/muo-assemblies.py" "$DIST/Data/assemblies.json" CustomContent.dll

# configuration: the template, the overlay over it (last wins), then the host values
install -d "$(dirname $DIST/Configuration/modernuo.json)"
cat > $DIST/Configuration/modernuo.json <<'MUO_EOF_0'
{
  "assemblyDirectories": [
    "./Assemblies"
  ],
  "dataDirectories": [
    ""
  ],
  "listeners": [
    "127.0.0.1:2610"
  ],
  "settings": {
    "accountHandler.enableAutoAccountCreation": "True",
    "accountHandler.enablePlayerPasswordCommand": "False",
    "accountHandler.maxAccountsPerIP": "16",
    "accountSecurity.encryptionAlgorithm": "Argon2",
    "assistants.enableNegotiation": "False",
    "autoArchive.archiveLocally": "True",
    "autoArchive.archivePath": "Archives",
    "autoArchive.backupMaxAge": "30",
    "autoArchive.backupPath": "Backups",
    "autoArchive.compressionLevel": "3",
    "autoArchive.dailyRetention": "30",
    "autoArchive.enableArchivePruning": "True",
    "autoArchive.hourlyRetention": "24",
    "autoArchive.monthlyRetention": "12",
    "autoArchive.retryCount": "3",
    "autoArchive.retryDelayMs": "500",
    "autoArchive.verifyArchives": "True",
    "autosave.enabled": "True",
    "autosave.saveDelay": "00:05:00",
    "autosave.warningDelay": "00:00:00",
    "buffIcons.enable": "True",
    "bulletinboards.creationTimeDelay": "00:02:00",
    "bulletinboards.expireDuration": "06:00:00",
    "bulletinboards.replyDelay": "00:00:30",
    "chat.enabled": "False",
    "clientVerification.ageLeniency": "10.00:00:00",
    "clientVerification.enable": "True",
    "clientVerification.gameTimeLeniency": "1.01:00:00",
    "clientVerification.invalidClientResponse": "Kick",
    "clientVerification.kickDelay": "00:00:20",
    "commandsystem.prefix": "[",
    "crashGuard.enabled": "True",
    "crashGuard.generateReport": "True",
    "crashGuard.restartServer": "True",
    "crashGuard.saveBackup": "True",
    "ethics.enable": "False",
    "houseDecay.enable": "True",
    "movement.delay.runFoot": "200",
    "movement.delay.runMount": "100",
    "movement.delay.turn": "0",
    "movement.delay.walkFoot": "400",
    "movement.delay.walkMount": "200",
    "movementThrottle.definiteRateThreshold": "1.100000023841858",
    "movementThrottle.hardQueueLimit": "10",
    "movementThrottle.maxChainGap": "2000",
    "movementThrottle.maxCredit": "200",
    "movementThrottle.maxRttBonus": "150",
    "movementThrottle.minSamplesForRate": "8",
    "movementThrottle.movementHistorySize": "20",
    "movementThrottle.speedHackNotificationCooldown": "300000",
    "movementThrottle.suspiciousRateThreshold": "1.0499999523162842",
    "murderSystem.bountiesEnabled": "False",
    "murderSystem.bountyExpiry": "14.00:00:00",
    "murderSystem.longTermMurderDuration": "1.16:00:00",
    "murderSystem.recentlyReportedDelay": "00:10:00",
    "murderSystem.shortTermMurderDuration": "08:00:00",
    "network.initialBufferSlabs": "1",
    "network.maxBufferSlabs": "128",
    "network.maxOutstandingSends": "32",
    "network.memoryCeilingPercent": "80",
    "network.sendBufferGrowthBudget": "268435456",
    "network.sendBufferMaxSize": "2097152",
    "network.sendBufferSize": "262144",
    "pages.discordWebhookUrl": null,
    "pathfinding.enable": "True",
    "pathfinding.maxResidentChunks": "8192",
    "pathfinding.maxSearchNodes": "1000",
    "pathfinding.recorder.enable": "False",
    "pathfinding.recorder.path": null,
    "pingServer.enabled": "True",
    "questSystem.enableMLQuests": "True",
    "serverListing.address": null,
    "serverListing.autoDetect": "True",
    "serverListing.serverName": "Demo \"Shard\"",
    "stamina.additionalLossWhenBelow": "0.1",
    "stamina.baseOverweightLoss": "5",
    "stamina.cannotRunWhenFatigued": "False",
    "stamina.cannotWalkWhenFatigued": "False",
    "stamina.enableMountStamina": "True",
    "stamina.stonesOverweightAllowance": "4",
    "stamina.stonesPerOverweightLoss": "25",
    "stats.gainChanceMultiplier": "1",
    "stats.statMax": "125",
    "testCenter.enable": "False",
    "timer.initialPoolCapacity": "1024",
    "timer.maxPoolCapacity": "16384",
    "uogateway.enabled": "True",
    "vetRewards.enable": "True",
    "vetRewards.rewardInterval": "30.00:00:00",
    "vetRewards.skillCapRewards": "True",
    "world.enableAutoRestart": "False",
    "world.savePath": "Saves",
    "world.useMultithreadedSaves": "True"
  }
}
MUO_EOF_0
chmod 0644 $DIST/Configuration/modernuo.json
install -d "$(dirname $DIST/Configuration/Custom.json)"
cat > $DIST/Configuration/Custom.json <<'MUO_EOF_0'
{"motd": "Welcome", "contact": "{{MUO_CONTACT}}"}
MUO_EOF_0
chmod 0644 $DIST/Configuration/Custom.json
install -d "$(dirname $DIST/Configuration/expansion.json)"
cat > $DIST/Configuration/expansion.json <<'MUO_EOF_0'
{"expansion": "ML"}
MUO_EOF_0
chmod 0644 $DIST/Configuration/expansion.json
install -d "$(dirname $BASE/muo-fill.py)"
cat > $BASE/muo-fill.py <<'MUO_EOF_0'
import os, sys
root = sys.argv[1]
for rel in sys.argv[2:]:
    path = os.path.join(root, rel)
    text = open(path, encoding="utf-8").read()
    out, rest = [], text
    while "{{" in rest:
        head, _, tail = rest.partition("{{")
        key, sep, rest = tail.partition("}}")
        if not sep or not key.isidentifier() or key not in os.environ:
            sys.exit("muo_shard: " + rel + " uses {{" + key + "}}, which is not set in the host env file")
        out += [head, os.environ[key]]
    out.append(rest)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write("".join(out))
MUO_EOF_0
chmod 0644 $BASE/muo-fill.py
install -d "$(dirname $BASE/muo-finish-config.py)"
cat > $BASE/muo-finish-config.py <<'MUO_EOF_0'
import json, sys
path, data, version = sys.argv[1:4]
doc = json.load(open(path, encoding="utf-8"))
doc["dataDirectories"] = [data]
if version:
    doc.setdefault("settings", {})["clientData.clientVersion"] = version
with open(path, "w", encoding="utf-8", newline="\n") as f:
    json.dump(doc, f, indent=2)
    f.write("\n")
MUO_EOF_0
chmod 0644 $BASE/muo-finish-config.py
python3 "$BASE/muo-fill.py" "$DIST/Configuration" Custom.json expansion.json
python3 "$BASE/muo-finish-config.py" "$DIST/Configuration/modernuo.json" "$DEMO_DATA" 7.0.107.76

# start script: the unit's one ExecStart; maps the host keys to the names the shard reads
install -d "$(dirname $DIST/muo-run.sh)"
cat > $DIST/muo-run.sh <<'MUO_EOF_0'
#!/bin/sh
# generated by muo_shard
# the dev-only GM account list in the patch is never honoured by a deployed shard
unset UO_SHARD_GM_ACCOUNTS
if [ -n "${DEMO_ADMIN:-}" ]; then export UO_SHARD_OWNER="$DEMO_ADMIN"; fi
if [ -n "${DEMO_ADMIN_PW:-}" ]; then export UO_SHARD_OWNER_PASSWORD="$DEMO_ADMIN_PW"; fi
cd "$(dirname "$0")"
exec ./ModernUO
MUO_EOF_0
chmod 0755 $DIST/muo-run.sh

# client data manifest, checked now and before every start
install -d "$(dirname $DIST/muo-verify-data.py)"
cat > $DIST/muo-verify-data.py <<'MUO_EOF_0'
#!/usr/bin/env python3
"""Refuse a client data folder that does not match the manifest (muo_shard data_manifest)."""
import hashlib, json, os, sys

base = os.environ.get("DEMO_DATA")
if not base or not os.path.isdir(base):
    sys.exit("muo_shard: DEMO_DATA is not set to a folder")
here = os.path.dirname(os.path.abspath(__file__))
manifest = json.load(open(os.path.join(here, "muo-data-manifest.json"), encoding="utf-8"))
mode = "size"
bad = []
for name, want in sorted(manifest["files"].items()):
    path = os.path.join(base, name)
    if not os.path.isfile(path):
        bad.append(name + ": missing")
    elif mode == "size":
        if os.path.getsize(path) != want["size"]:
            bad.append(name + ": size " + str(os.path.getsize(path)) + ", expected " + str(want["size"]))
    else:
        h = hashlib.sha256()
        with open(path, "rb") as f:
            for chunk in iter(lambda: f.read(1 << 20), b""):
                h.update(chunk)
        if h.hexdigest() != want["sha256"]:
            bad.append(name + ": sha256 differs")
if bad:
    sys.exit("muo_shard: client data does not match the manifest:\n  " + "\n  ".join(bad))
MUO_EOF_0
chmod 0755 $DIST/muo-verify-data.py
install -d "$(dirname $DIST/muo-data-manifest.json)"
cat > $DIST/muo-data-manifest.json <<'MUO_EOF_0'
{
  "files": {
    "map0.mul": {
      "sha256": "0000000000000000000000000000000000000000000000000000000000000000",
      "size": 77
    }
  }
}
MUO_EOF_0
chmod 0644 $DIST/muo-data-manifest.json
python3 "$DIST/muo-verify-data.py"

# content packs, after the build
as_user python3 "$MUO_REPO/tools/shard_content/run.py" deploy --shard-dir "$DIST" --name 'Demo Shard' --catalogue https://example.invalid/catalogue --pack demo --version 1.0.0

# ownership; Saves/ and Logs/ are never touched
chown -R "$SVC_USER:$SVC_USER" "$DIST"

# the service
install -d "$(dirname $BASE/unit.new)"
cat > $BASE/unit.new <<'MUO_EOF_0'
[Unit]
Description=ModernUO shard Demo "Shard" (muo-demo-shard.service)
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=demo-svc
Group=demo-svc
WorkingDirectory=/srv/muo/demo-shard/dist
EnvironmentFile=/etc/muo/demo-shard.env
ExecStartPre=/usr/bin/python3 "/srv/muo/demo-shard/dist/muo-verify-data.py"
ExecStartPre="python3" "/srv/muo/demo-shard/src/tools/shard_data/run.py" "--data" "${DEMO_DATA}" "--out" "/srv/muo/demo-shard/dist/Data" "--note" "100%% \"sure\" $$HOME"
ExecStart=/srv/muo/demo-shard/dist/muo-run.sh
Restart=always
RestartSec=5
NoNewPrivileges=true
PrivateTmp=true
MemoryMax=6G

[Install]
WantedBy=multi-user.target
MUO_EOF_0
chmod 0644 $BASE/unit.new
if have_systemd; then
    if ! cmp -s "$BASE/unit.new" "/etc/systemd/system/$UNIT"; then
        install -m 0644 "$BASE/unit.new" "/etc/systemd/system/$UNIT"
        systemctl daemon-reload
    fi
    systemctl enable "$UNIT"
    systemctl restart "$UNIT"
else
    echo "muo_shard: no systemd here (a container?). Start the shard with:"
    echo "  set -a; . $ENV_FILE; set +a; runuser -u $SVC_USER -- $DIST/muo-run.sh"
fi
rm -f "$BASE/unit.new"
echo "muo_shard: deploy of $ID done at ${PIN:0:9}; check with: run.py plan status"
