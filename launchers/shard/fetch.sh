#!/usr/bin/env bash
# Clones ModernUO at the pinned commit (or moves to a new pin) and applies the GUO patches, printing each one.
set -euo pipefail
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
head="$(git -C "$src" rev-parse -q --verify HEAD 2>/dev/null || true)"
want="$(git -C "$src" rev-parse -q --verify "$UO_SHARD_REF^{commit}" || true)"
# A fresh --no-checkout clone has a HEAD but an empty index: nothing is on it, so
# there are no patches to take off (resetting a file absent from the index fails).
if [ -z "$(git -C "$src" ls-files 2>/dev/null | head -n 1)" ]; then head=""; fi
if [ -z "$want" ]; then
    echo "[shard] The pin $UO_SHARD_REF is not in $UO_SHARD_REPO."
    exit 1
fi

if [ "$head" != "$want" ]; then
    if [ -n "$head" ]; then
        echo "[shard] Moving from ${head:0:9} to the pin ${want:0:9}: taking the patches off first"
        for ((i = ${#patches[@]} - 1; i >= 0; i--)); do
            p="${patches[i]}"
            if git -C "$src" apply -R --check "$p" >/dev/null 2>&1; then
                git -C "$src" apply -R "$p" || exit 1
            else
                # Not cleanly reversible (an older copy of the patch, or edits on top):
                # put its files back to HEAD; a file the patch adds is not in HEAD, so it goes.
                echo "[shard]   $(basename "$p") does not come off in reverse; resetting its files"
                while IFS=$'	' read -r _ _ f; do
                    if git -C "$src" cat-file -e "HEAD:$f" 2>/dev/null; then
                        git -C "$src" checkout -q -- "$f" || exit 1
                    else
                        rm -f -- "$src/$f"
                    fi
                done < <(git -C "$src" apply --numstat "$p")
            fi
        done
    fi
    git -C "$src" checkout -q --detach "$want" || {
        echo "[shard] Checkout refused: the checkout has other local changes. See tools/modernuo/README.md, \"Retired\"."
        exit 1
    }
    echo "[shard] At the pin ${want:0:9}"
else
    # Same pin, run again: take the stack off, last patch first, and put it back. A patch
    # under a later one that edits the same lines does not come off alone, so patches
    # cannot be judged one at a time on a patched tree. This also applies UO_SHARD_DFI.
    for ((i = ${#patches[@]} - 1; i >= 0; i--)); do
        p="${patches[i]}"
        if git -C "$src" apply -R --check "$p" >/dev/null 2>&1; then
            git -C "$src" apply -R "$p" || exit 1
        fi
    done
fi

# UO_SHARD_DFI=0: the DFI shard content patches stay off.
is_dfi() { case "$(basename "$1")" in *-dfi-*) return 0 ;; *) return 1 ;; esac; }

for p in "${patches[@]}"; do
    if [ "${UO_SHARD_DFI:-1}" = "0" ] && is_dfi "$p"; then
        echo "[shard] Leaving $(basename "$p") off: UO_SHARD_DFI=0"
        continue
    fi
    echo "[shard] Applying $(basename "$p")"
    if git -C "$src" apply --check "$p" >/dev/null 2>&1; then
        git -C "$src" apply "$p" || exit 1
    elif git -C "$src" apply -R --check "$p" >/dev/null 2>&1; then
        echo "[shard]   already applied, skipping"
    else
        # Neither applies nor comes off: the checkout holds something else (an older
        # copy of this patch, local edits). Show git's reason and stop; never build on it.
        git -C "$src" apply --check "$p" || true   # show git's reason; set -e must not stop us before the FATAL line
        echo "[shard] FATAL: $(basename "$p") does not apply to the checkout at ${want:0:9}, and is not already applied; stopping"
        exit 1
    fi
done

echo "[shard] Done. Next: launchers/shard/build.sh"
