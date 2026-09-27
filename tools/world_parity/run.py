#!/usr/bin/env python3
"""Compare the editor's World view with a logged-in client's frame, pixel by pixel.

docs/editor_plan.md phase 2's check: the editor's view of a place must be
what the client shows there. Takes two pictures of one cell:

  editor  the UO World tab (addons/guo_editor) drawn at exactly the client's
          world-view size, from a fresh, empty world project, so the install
          alone is drawn;
  client  GUO played as a GM lane account on the PRIVATE shard by default
          (tools/editor_shard, 127.0.0.1:2594; the shared port is refused
          without --allow-shared), (the last of
          UO_SHARD_GM_ACCOUNTS, as tools/multi_client does it), with
          "[go X Y" typed in game, then photographed. Not "[globallight":
          that changes the light for every player on a shared shard.

and diffs them: the editor frame is found in the client frame by search, the
player's own body at the centre is masked, and what still differs is counted
and located.

    python tools/world_parity/run.py [--at 1164,1668] [--client-root DIR]
                                     [--files-override FILE] [--client-only]

What cannot match, and why: the client draws what the shard sends (mobiles,
items, server-placed decoration and houses) and the editor does not; the
residual is reported as clusters so each can be looked at, not hidden.

--files-override points the client at an export's files_override.txt
(tools/world): with --client-only that is the "client sees the exported
edit" check. The client's home (settings, profiles) is a scratch folder under
the output, so no player's settings are touched. Nothing here writes the
install or the client checkout.

Exit codes: 0 both pictures taken and compared, 1 a picture failed, 2 bad input.
"""

from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
from pathlib import Path

import numpy as np
from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from guo import load_config  # noqa: E402
from guo.config import parse_config_bat  # noqa: E402
from guo.process import no_activate  # noqa: E402

# Profile.GameWindowPosition / GameWindowSize defaults: a fresh client home
# draws the world here, inside the window.
VIEW_AT = (10, 10)
VIEW_SIZE = (600, 480)

# What a fresh client home puts over the world, in world-view pixels: logging
# in double-clicks the player, the server opens the paperdoll there, and the
# chat entry line runs along the bottom. Masked by name and counted apart.
UI_MASKS = {
    "paperdoll gump": (86, 86, 358, 418),
    "chat entry line": (0, 460, 600, 480),
}

SEASONS = ["spring", "summer", "fall", "winter", "desolation"]


def shard_season(client_root: Path, facet: int) -> str | None:
    """The season the dev shard gives a map (ModernUO map-definitions.json), if it can be read."""
    path = client_root / "tools" / "modernuo" / "src" / "Distribution" / "Data" / "map-definitions.json"
    try:
        maps = json.loads(path.read_text(encoding="utf-8"))
        return SEASONS[int(maps[facet]["season"])]
    except (OSError, ValueError, KeyError, IndexError, TypeError):
        return None


def editor_shot(cfg, x: int, y: int, facet: int, out: Path, season: str, world_project: Path | None = None) -> Path | None:
    project = cfg.godot_project
    project_godot = project / "project.godot"
    before = project_godot.read_bytes()
    # The install alone, unless a world project is to be drawn over it.
    world = world_project or out / "empty_world_project"
    env = {**os.environ, "UO_WORLD_PROJECT": str(world), "UO_CLIENT_DATA": str(cfg.client_data)}
    cmd = [str(cfg.godot_console_exe), "--editor", "--path", str(project), "--",
           "--guo-editor-smoke", str(out / "editor"),
           "--guo-editor-world-shot", f"{facet},{x},{y},{VIEW_SIZE[0]},{VIEW_SIZE[1]},{season}"]
    print(f"[parity] editor: {' '.join(cmd[:6])} ...")
    try:
        with (out / "editor.log").open("w", encoding="utf-8", errors="replace") as log:
            subprocess.run(cmd, stdout=log, stderr=subprocess.STDOUT, env=env, timeout=600, **no_activate())
    finally:
        if project_godot.read_bytes() != before:
            project_godot.write_bytes(before)
    shot = out / "editor" / "world_shot.png"
    return shot if shot.exists() else None


def gm_lane(client_root: Path) -> str:
    values = parse_config_bat(client_root / "launchers" / "_shared" / "config.bat")
    accounts = (os.environ.get("UO_SHARD_GM_ACCOUNTS") or values.get("UO_SHARD_GM_ACCOUNTS") or "").split(",")
    accounts = [a.strip() for a in accounts if a.strip()]
    if not accounts:
        sys.exit("[parity] no UO_SHARD_GM_ACCOUNTS in the client checkout's config.bat")
    return accounts[-1]


def client_shot(cfg, client_root: Path, x: int, y: int, out: Path, override: Path | None, account: str, port: int) -> Path | None:
    home = out / "client_home"
    if home.exists():
        shutil.rmtree(home)
    (home / "cache").mkdir(parents=True)
    profiles = home / "profiles"
    profiles.mkdir()
    settings = {"profilespath": str(profiles)}
    if override is not None:
        # Upstream's own setting: settings.json "files_override" -> UOFilesOverrideMap.
        settings["files_override"] = str(override)
    (home / "settings.json").write_text(json.dumps(settings), encoding="utf-8")
    # A fresh profile starts from default.json: no top bar over the world.
    (profiles / "default.json").write_text(json.dumps({"topbar_gump_is_disabled": True}), encoding="utf-8")

    console = client_root / "tools" / "godot" / "godot-console.cmd"
    cmd = [str(console), "--path", str(client_root / "godot" / "GUO"), "--",
           "--play", "--account", account, "--password", account, "--character", account.capitalize(),
           "--window-position", "40,40", "--window-size", "1024,768",
           "--shard-command", f"[go {x} {y}",
           "--screenshot-dir", str(out / "client"), "--screenshot-name", "client"]
    env = {**os.environ, "UO_CLIENT_DATA": str(cfg.client_data), "UO_CACHE_DIR": str(home / "cache"),
           "UO_CLIENT_VERSION": cfg.client_version, "UO_SHARD_HOST": "127.0.0.1", "UO_SHARD_PORT": str(port)}
    print(f"[parity] client ({client_root.name}, {account}): [go {x} {y}" + (f", files_override {override}" if override else ""))
    (out / "client").mkdir(parents=True, exist_ok=True)
    with (out / "client.log").open("w", encoding="utf-8", errors="replace") as log:
        code = subprocess.run(cmd, stdout=log, stderr=subprocess.STDOUT, env=env, timeout=600, **no_activate()).returncode
    shots = sorted((out / "client").glob("client*.png"))
    if code != 0:
        print(f"[parity] client exited {code}; see {out / 'client.log'}")
    return shots[-1] if shots else None


def align(client: np.ndarray, editor: np.ndarray) -> tuple[int, int, float]:
    """Where the editor frame sits in the client frame: the offset with the most equal pixels."""
    h, w = editor.shape[:2]
    best = (0, 0, -1.0)
    # A central patch, compared at every offset near the expected one.
    ph, pw = h // 3, w // 3
    patch = editor[ph:2 * ph, pw:2 * pw, :3]
    for oy in range(max(0, VIEW_AT[1] - 40), min(client.shape[0] - h, VIEW_AT[1] + 40) + 1):
        for ox in range(max(0, VIEW_AT[0] - 40), min(client.shape[1] - w, VIEW_AT[0] + 40) + 1):
            region = client[oy + ph:oy + 2 * ph, ox + pw:ox + 2 * pw, :3]
            score = float(np.mean(np.all(region == patch, axis=2)))
            if score > best[2]:
                best = (ox, oy, score)
    return best


def clusters(diff: np.ndarray, cell: int = 16) -> list[dict]:
    """Differing pixels grouped by cell and joined into connected boxes."""
    h, w = diff.shape
    gh, gw = (h + cell - 1) // cell, (w + cell - 1) // cell
    grid = np.zeros((gh, gw), dtype=np.int64)
    ys, xs = np.nonzero(diff)
    np.add.at(grid, (ys // cell, xs // cell), 1)
    seen = np.zeros_like(grid, dtype=bool)
    found = []
    for gy in range(gh):
        for gx in range(gw):
            if grid[gy, gx] == 0 or seen[gy, gx]:
                continue
            stack, cells = [(gy, gx)], []
            seen[gy, gx] = True
            while stack:
                cy, cx = stack.pop()
                cells.append((cy, cx))
                for ny, nx in ((cy + 1, cx), (cy - 1, cx), (cy, cx + 1), (cy, cx - 1)):
                    if 0 <= ny < gh and 0 <= nx < gw and grid[ny, nx] and not seen[ny, nx]:
                        seen[ny, nx] = True
                        stack.append((ny, nx))
            cy0 = min(c[0] for c in cells); cy1 = max(c[0] for c in cells)
            cx0 = min(c[1] for c in cells); cx1 = max(c[1] for c in cells)
            found.append({"box": [cx0 * cell, cy0 * cell, (cx1 + 1) * cell, (cy1 + 1) * cell],
                          "pixels": int(sum(grid[c] for c in cells))})
    return sorted(found, key=lambda c: -c["pixels"])


def compare(client_png: Path, editor_png: Path, out: Path) -> dict:
    client = np.asarray(Image.open(client_png).convert("RGBA"))
    editor = np.asarray(Image.open(editor_png).convert("RGBA"))
    ox, oy, score = align(client, editor)
    h, w = editor.shape[:2]
    region = client[oy:oy + h, ox:ox + w, :3]
    diff = np.any(region != editor[:, :, :3], axis=2)

    # The player's own body stands on the centre cell: the client draws it,
    # the editor's stand-in has none. A human is about 60 px wide and 100 tall
    # from the cell's centre up.
    mask = np.zeros_like(diff)
    cx, cy = w // 2, h // 2
    mask[max(0, cy - 110):cy + 25, max(0, cx - 40):cx + 40] = True
    player_px = int((diff & mask).sum())

    ui = np.zeros_like(diff)
    for x0, y0, x1, y1 in UI_MASKS.values():
        ui[y0:y1, x0:x1] = True
    ui &= ~mask
    ui_px = int((diff & ui).sum())
    mask |= ui

    total = int(diff.size)
    raw = int(diff.sum())
    masked = diff & ~mask
    residual = int(masked.sum())
    compared = int((~mask).sum())

    vis = editor[:, :, :3].copy() // 3
    vis[masked] = (255, 0, 255)
    vis[mask & diff] = (0, 160, 255)
    Image.fromarray(vis.astype(np.uint8)).save(out / "diff.png")
    side = np.concatenate([region, editor[:, :, :3], vis], axis=1)
    Image.fromarray(side.astype(np.uint8)).save(out / "side_by_side.png")

    return {
        "offset": [ox, oy], "alignment_score": round(score, 4),
        "pixels": total,
        "differ_raw": raw, "identical_raw_pct": round(100 * (1 - raw / total), 3),
        "masked_px": int(mask.sum()),
        "differing_under_player_mask": player_px,
        "differing_under_ui_masks": ui_px, "ui_masks": UI_MASKS,
        "compared_px": compared,
        "residual": residual, "identical_after_mask_pct": round(100 * (1 - residual / compared), 3),
        "residual_clusters": clusters(masked)[:12],
    }


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--at", default="1164,1668", help="cell x,y on map0")
    ap.add_argument("--client-root", type=Path, help="checkout whose client plays (default this one)")
    ap.add_argument("--files-override", type=Path, help="client reads an export (tools/world files_override.txt)")
    ap.add_argument("--client-only", action="store_true", help="only the client shot (export proof)")
    ap.add_argument("--out", type=Path)
    ap.add_argument("--windowed", action="store_true",
                    help="required: both pictures need windows (an editor, then a client), which take the "
                         "desktop's focus while they run. Only when the person at the machine agrees")
    ap.add_argument("--season", choices=SEASONS, help="editor season (default: the shard's for map0)")
    ap.add_argument("--shard-port", type=int, default=2594,
                    help="shard the client plays on: default the private instance (tools/editor_shard)")
    ap.add_argument("--allow-shared", action="store_true",
                    help="permit the shared dev shard's port; agree the account with its users first")
    ap.add_argument("--project", type=Path, help="editor draws this world project over the install (pair with --files-override)")
    args = ap.parse_args()

    cfg = load_config()
    if not args.windowed:
        print("[parity] needs --windowed: it photographs an editor window and a client window, and each takes "
              "the desktop's focus while it runs. Run it only when the person at the machine agrees.")
        return 2
    if args.shard_port == cfg.shard_port and not args.allow_shared:
        print(f"[parity] REFUSED: port {args.shard_port} is the shared dev shard. Use the private instance "
              "(tools/editor_shard, 2594), or --allow-shared after agreeing an account with its users.")
        return 2
    x, y = (int(v) for v in args.at.split(","))
    client_root = (args.client_root or cfg.root).resolve()
    tag = f"{x}_{y}" + ("_override" if args.files_override else "") + ("_project" if args.project else "")
    out = (args.out or cfg.build / "world_parity" / tag).resolve()
    if out.exists():
        shutil.rmtree(out)
    out.mkdir(parents=True)

    report: dict = {"at": [0, x, y], "client_root": str(client_root)}
    account = gm_lane(client_root)
    report["account"] = account

    season = args.season or shard_season(client_root, 0) or "summer"
    report["season"] = season

    editor = None
    if not args.client_only:
        editor = editor_shot(cfg, x, y, 0, out, season, args.project.resolve() if args.project else None)
        report["editor_project"] = str(args.project) if args.project else None
        report["editor_png"] = str(editor) if editor else None
        if editor is None:
            print(f"[parity] FAILED: no editor shot; see {out / 'editor.log'}")
            return 1

    override = args.files_override.resolve() if args.files_override else None
    client = client_shot(cfg, client_root, x, y, out, override, account, args.shard_port)
    report["client_png"] = str(client) if client else None
    if client is None:
        print(f"[parity] FAILED: no client shot; see {out / 'client.log'}")
        (out / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
        return 1

    if editor is not None:
        report.update(compare(client, editor, out))
        print(f"[parity] aligned at {report['offset']} (score {report['alignment_score']})")
        print(f"[parity] season {season}; identical: {report['identical_raw_pct']}% raw; "
              f"masked: player {report['differing_under_player_mask']} px, UI {report['differing_under_ui_masks']} px differing")
        print(f"[parity] residual {report['residual']} of {report['compared_px']} compared px "
              f"({report['identical_after_mask_pct']}% identical) in {len(report['residual_clusters'])} cluster(s)")
        for c in report["residual_clusters"][:6]:
            print(f"[parity]   residual box {c['box']}: {c['pixels']} px")

    (out / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(f"[parity] report: {out / 'report.json'}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
