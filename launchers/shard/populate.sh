#!/usr/bin/env bash
# Generate the dev shard's world from the client, as the owner. Twin of populate.bat.
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
