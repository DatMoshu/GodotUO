#!/usr/bin/env python3
"""Ask whether a `rewrite` file is really a rewrite.

The audit tiers a file by what it IMPORTS. That is the right default -- it is
cheap, it never lies about direction, and it was enough to get four fifths of
the port moved mechanically. But an import is not a use. ClassicUO carries a
number of `using Microsoft.Xna.Framework.Input;` lines in files that never
name a type from it, and the audit dutifully calls every one of them a
rewrite.

That matters now rather than earlier, because the rewrite tier is what is
left. A file wrongly in it is not just miscounted; it is work nobody starts,
because the tier says it needs a renderer that does not exist yet.

So this reads the bodies. For each rewrite-tier file it reports:

  * which FNA types it actually names, and
  * whether the answer is "none", which makes it mechanical after all.

It is deliberately separate from the audit. The audit is a score and must stay
boring and reproducible; this is a diagnostic that makes a judgement call, and
its output is a list of candidates for a human to confirm -- not a new tier.

Usage:
    python tools/port_triage/run.py
    python tools/port_triage/run.py --area Game/UI
    python tools/port_triage/run.py --show-types      # what to build, ranked
"""

from __future__ import annotations

import argparse
import importlib.util as _ilu
import re
import sys
from collections import Counter, defaultdict
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from guo import load_config  # noqa: E402

_here = Path(__file__).resolve().parents[1]


def _load(name: str, rel: str):
    spec = _ilu.spec_from_file_location(name, _here / rel)
    mod = _ilu.module_from_spec(spec)
    spec.loader.exec_module(mod)  # type: ignore[union-attr]
    return mod


_audit = _load("guo_port_audit", "port_audit/run.py")
_bulk = _load("guo_port_bulk", "port_bulk/run.py")

area_for = _audit.area_for
classify = _audit.classify
REPLACED_BY_ENGINE = set(_audit.REPLACED_BY_ENGINE)
split_code_and_text = _bulk._split_code_and_text


# Types that only FNA provides. Naming one is what makes a file a genuine
# rewrite; importing the namespace that holds them is not.
#
# GUO.Compat's value types are deliberately absent -- Point, Rectangle, Color
# and the vectors are exactly the types the shim tier exists to serve. Matrix
# IS listed: Compat does not provide it, so a file naming it needs a decision.
FNA_TYPES = {
    # Graphics: device and pipeline state
    "GraphicsDevice", "GraphicsDeviceManager", "PresentationParameters",
    "DisplayMode", "GraphicsAdapter", "Viewport",
    "BlendState", "SamplerState", "RasterizerState", "DepthStencilState",
    "Blend", "BlendFunction", "CompareFunction", "StencilOperation",
    "CullMode", "FillMode", "TextureFilter", "TextureAddressMode",
    # Graphics: resources
    "Texture", "Texture2D", "Texture3D", "TextureCube", "RenderTarget2D",
    "VertexBuffer", "IndexBuffer", "DynamicVertexBuffer",
    "DynamicIndexBuffer", "VertexDeclaration", "VertexElement",
    "VertexPositionColorTexture", "VertexPositionTexture",
    "VertexPositionColor", "IVertexType",
    "SurfaceFormat", "DepthFormat", "PrimitiveType", "IndexElementSize",
    "BufferUsage", "SpriteBatch", "SpriteEffects", "SpriteFont",
    "SpriteSortMode", "Effect", "BasicEffect", "EffectParameter",
    "EffectTechnique", "EffectPass",
    # Math that Compat does not shim
    "Matrix",
    # Input
    "Keys", "KeyboardState", "MouseState", "Mouse", "GamePad",
    "GamePadState", "GamePadCapabilities", "Buttons", "ButtonState",
    "TouchPanel", "TouchCollection",
    # Audio / media
    "SoundEffect", "SoundEffectInstance", "DynamicSoundEffectInstance",
    "AudioChannels", "Song", "MediaPlayer", "MediaState", "VideoPlayer",
    # Framework host
    "Game", "GameTime", "GameWindow", "GameComponent", "DrawableGameComponent",
    "Content", "ContentManager",
}

# `Game` is a ClassicUO namespace as well as an FNA base class, and the port
# has its own Game area, so bare mentions are meaningless. Only count it when
# it appears as a base class or a typed member, which is rare enough to check
# by hand from the report.
AMBIGUOUS = {"Game", "Content", "Texture", "Effect", "Mouse"}

HEAVY_IMPORT = re.compile(
    r"using\s+Microsoft\.Xna\.Framework\.(Graphics|Input|Audio|Media|Content)\s*;"
)


def code_only(text: str) -> str:
    """The file with strings, chars and comments removed."""
    return "".join(c for c, is_code in split_code_and_text(text) if is_code)


def types_used(text: str) -> Counter:
    body = code_only(text)
    found = Counter()
    for name in FNA_TYPES:
        n = len(re.findall(r"\b%s\b" % re.escape(name), body))
        if n:
            found[name] = n
    return found


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(prog="port_triage")
    ap.add_argument("--area", action="append", default=[])
    ap.add_argument("--show-types", action="store_true",
                    help="rank the FNA types the rewrite tier still needs")
    args = ap.parse_args(argv)

    cfg = load_config()
    upstream_src = cfg.upstream / "src"
    if not upstream_src.is_dir():
        print(f"[triage] FATAL: upstream not found: {upstream_src}")
        return 2

    wanted = set(args.area)
    clean: list[tuple[str, int, str]] = []
    dirty: list[tuple[str, int, Counter]] = []
    all_types: Counter = Counter()
    by_type_files: dict[str, set] = defaultdict(set)

    for path in sorted(upstream_src.rglob("*.cs")):
        if {"obj", "bin"} & set(path.parts):
            continue
        rel = path.relative_to(upstream_src)
        area = area_for(rel)
        if area in REPLACED_BY_ENGINE:
            continue
        if wanted and area not in wanted:
            continue

        text = path.read_text(encoding="utf-8", errors="replace")
        if classify(text, area, rel) != _audit.TIER_REWRITE:
            continue

        lines = text.count("\n") + 1
        used = types_used(text)
        unambiguous = Counter({k: v for k, v in used.items()
                               if k not in AMBIGUOUS})

        if not unambiguous:
            why = "no FNA type named"
            if used:
                why = "only ambiguous: " + ",".join(sorted(used))
            clean.append((rel.as_posix(), lines, why))
        else:
            dirty.append((rel.as_posix(), lines, unambiguous))
            all_types.update(unambiguous)
            for k in unambiguous:
                by_type_files[k].add(rel.as_posix())

    print(f"[triage] rewrite-tier files examined: {len(clean) + len(dirty)}")
    print(f"[triage]   name no FNA type : {len(clean):>3} "
          f"({sum(l for _, l, _ in clean):,} lines)")
    print(f"[triage]   genuinely bound  : {len(dirty):>3} "
          f"({sum(l for _, l, _ in dirty):,} lines)")

    if clean:
        print("\n[triage] Candidates to reclassify -- import FNA, never name it.")
        print("[triage] Read each one before trusting this; the check is textual.")
        for rel, lines, why in sorted(clean, key=lambda r: -r[1]):
            print(f"   {lines:>5}  {rel}")
            if not why.startswith("no FNA"):
                print(f"          {why}")

    if args.show_types:
        print("\n[triage] FNA types the remaining rewrite tier needs, "
              "by number of files:")
        for name, _ in all_types.most_common():
            files = by_type_files[name]
            print(f"   {len(files):>3} files, {all_types[name]:>4} uses  {name}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
