#!/usr/bin/env bash
# Logs the owner into the running dev shard and types every world generator command (doors, signs, spawners, decorations), then saves; takes a few minutes.
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1

echo "[populate] Generating the world on $UO_SHARD_HOST:$UO_SHARD_PORT"
echo "[populate] This takes a few minutes."

if "$GODOT_CONSOLE" --path "$UO_GODOT_PROJECT" -- --play \
    --shard-command "[TelGen" \
    --shard-command "[MoonGen" \
    --shard-command "[DoorGen" \
    --shard-command "[GenerateSpawners Data/Spawns/**/*.json" \
    --shard-command "[SignGen" \
    --shard-command "[Decorate" \
    --shard-command "[GenChamps" \
    --shard-command "[DecorateMag" \
    --shard-command "[GenStealArties" \
    --shard-command "[SHTelGen" \
    --shard-command "[SecretLocGen" \
    --shard-command "[GenLeverPuzzle" \
    --shard-command "[GenGauntlet" \
    --shard-command "[GenKhaldun" \
    --shard-command "[Save" "$@"
then
    echo "[populate] OK"
else
    echo "[populate] FAILED"
    exit 1
fi
