#!/usr/bin/env python3
r"""Build the Android launcher icons. Kept at this path for the README and
the habits that call it; the work now lives in tools\brand\run.py, which
builds the Android set together with the Windows icon and the boot splash
from the committed master design\brand\guo-sigil.png. No press kit, no
UO_ANDROID_BRAND_DIR: the build has no dependency outside the repository.

Usage:
    python tools\android\icons.py            (same as python tools\brand\run.py)
"""

from __future__ import annotations

import importlib.util
import sys
from pathlib import Path


def main(argv: list[str] | None = None) -> int:
    brand = Path(__file__).resolve().parents[1] / "brand" / "run.py"
    spec = importlib.util.spec_from_file_location("guo_brand_run", brand)
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(module)
    return module.main(argv)


if __name__ == "__main__":
    sys.exit(main())
