#!/usr/bin/env bash
# Runs the GUO client in a window against the configured shard (or the first-run wizard when no UO data is set) and stays open until the game exits.
# args: --offline | --frames <n> | <client flags>
set -euo pipefail
. "$(dirname "$0")/../_shared/common.sh" || exit 1

# Which data the client reads (ADR-0021); exit 3 means "no valid data", which
# is not an error: the client is started anyway and opens the wizard.
data_env="$UO_BUILD/datasources/play_env.sh"
rc=0
"$UO_PYTHON" "$UO_TOOLS/datasources/run.py" check --sh "$data_env" || rc=$?
if [ "$rc" = "3" ]; then
    echo "[play] No valid UO data yet: the client will open the first-run wizard."
elif [ "$rc" != "0" ]; then
    echo "[play] FATAL: tools/datasources failed (exit $rc)"
    exit 1
fi
# shellcheck disable=SC1090
. "$data_env"

echo "[play] Godot        : $GODOT_VERSION $GODOT_FLAVOR"
echo "[play] Project      : $UO_GODOT_PROJECT"
echo "[play] UO client data: ${UO_CLIENT_DATA:-}"
echo "[play] Data source  : ${UO_DATA_SOURCE:-}"
echo "[play] Shard        : $UO_SHARD_HOST:$UO_SHARD_PORT"
echo

quit_after=()
args=()
if [ -n "${UO_FILES_OVERRIDE:-}" ]; then args+=(--files-override "$UO_FILES_OVERRIDE"); fi
while [ $# -gt 0 ]; do
    case "$1" in
        --frames) quit_after=(--quit-after "$2"); shift 2 ;;
        *) args+=("$1"); shift ;;
    esac
done

# Other engine flags (a scripted run's --write-movie and --resolution) come in
# GUO_ENGINE_ARGS, split on spaces as play.bat does.
# shellcheck disable=SC2086
exec "$GODOT_CONSOLE" --path "$UO_GODOT_PROJECT" ${quit_after[@]+"${quit_after[@]}"} ${GUO_ENGINE_ARGS:-} -- ${args[@]+"${args[@]}"}
