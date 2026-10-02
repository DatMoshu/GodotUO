#!/usr/bin/env bash
# ============================================================================
#  Clone ModernUO at the pin (UO_SHARD_REF) and apply the GUO patches.
#  Twin of fetch.bat: run once on a fresh machine, and again after the pin
#  moves; it takes the patches off, checks out the new pin and puts them
#  back. Stop the shard first, and rebuild with build.sh afterwards.
#
#  A full clone, not a shallow one: ModernUO versions itself with
#  Nerdbank.GitVersioning, which walks the history.
# ============================================================================
GUO_NEEDS_GODOT=0 . "$(dirname "$0")/../_shared/common.sh" || exit 1
src="$UO_SHARD_SRC"
patches=("$UO_ROOT"/tools/modernuo/patches/*.patch)

if [ -d "$src/.git" ]; then
    echo "[shard] Already cloned: $src"
else
    echo "[shard] Cloning $UO_SHARD_REPO"
    git clone --no-checkout "$UO_SHARD_REPO" "$src" || exit 1
fi

git -C "$src" cat-file -e "$UO_SHARD_REF^{commit}" 2>/dev/null || git -C "$src" fetch origin || exit 1
head="$(git -C "$src" rev-parse -q --verify HEAD 2>/dev/null)"
want="$(git -C "$src" rev-parse -q --verify "$UO_SHARD_REF^{commit}")"
if [ -z "$want" ]; then
    echo "[shard] The pin $UO_SHARD_REF is not in $UO_SHARD_REPO."
    exit 1
fi

if [ "$head" != "$want" ]; then
    if [ -n "$head" ]; then
        echo "[shard] Moving from ${head:0:9} to the pin ${want:0:9}: taking the patches off first"
        for p in "${patches[@]}"; do
            git -C "$src" apply -R --check "$p" >/dev/null 2>&1 && git -C "$src" apply -R "$p"
        done
    fi
    git -C "$src" checkout -q --detach "$want" || {
        echo "[shard] Checkout refused: the checkout has other local changes. See tools/modernuo/README.md, \"Retired\"."
        exit 1
    }
    echo "[shard] At the pin ${want:0:9}"
fi

for p in "${patches[@]}"; do
    echo "[shard] Applying $(basename "$p")"
    if git -C "$src" apply --check "$p" >/dev/null 2>&1; then
        git -C "$src" apply "$p" || exit 1
    else
        echo "[shard]   already applied, skipping"
    fi
done

echo "[shard] Done. Next: launchers/shard/build.sh"
