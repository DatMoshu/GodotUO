"""NET-00c MUO patch: reproduce a loopback ban, then prove the fix on pinned sources.

No sockets, accounts or live shard files are changed. Requires the ModernUO pin
and .NET 10 (launchers/shard/fetch.bat and launchers/shard/build.bat).
"""
from __future__ import annotations

import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

from test_upstream import HERE, ROOT, main_checkout, shard_ref

TARGET = "Projects/UOContent/Network/BanExemptions.cs"
SOURCES = (
    TARGET,
    "Projects/UOContent/Network/AutoDenylist/AutoDenylist.cs",
    "Projects/Server/Network/Bans/BanChannel.cs",
    "Projects/Server/Network/Bans/BanReasons.cs",
    "Projects/Server/Network/Bans/IBanReporter.cs",
    "Projects/Server/Network/IConnectionFilter.cs",
)


class LoopbackBanTests(unittest.TestCase):
    def test_silent_loopback_report_and_patch(self):
        dotnet = shutil.which("dotnet")
        if not dotnet:
            self.skipTest("no dotnet; run launchers/shard/build.bat")
        candidates = [ROOT / "tools/modernuo/src"]
        if os.environ.get("UO_SHARD_SRC"):
            candidates.insert(0, Path(os.environ["UO_SHARD_SRC"]))
        main = main_checkout(ROOT)
        if main:
            candidates.append(main / "tools/modernuo/src")
        src = next((p for p in candidates if (p / ".git").exists()), None)
        if src is None:
            self.skipTest("no ModernUO pin; run launchers/shard/fetch.bat")
        # Read-only trust for this command only, never a global safe.directory change.
        git = ["git", "-c", f"safe.directory={src.as_posix()}", "-C", str(src)]
        with tempfile.TemporaryDirectory(prefix="guo-loopback-") as tmp:
            root = Path(tmp)
            for rel in SOURCES:
                data = subprocess.run(git + ["show", f"{shard_ref()}:{rel}"],
                                      capture_output=True, check=True).stdout
                path = root / rel
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(data)
            harness = HERE / "tests/loopback_bans"
            for name in ("Program.cs", "Stubs.cs"):
                shutil.copy(harness / name, root / name)
            shutil.copy(harness / "LoopbackBans.csproj.txt", root / "LoopbackBans.csproj")
            # The harness has no packages; avoid user feeds and credentials entirely.
            (root / "NuGet.Config").write_text(
                '<configuration><packageSources><clear /></packageSources></configuration>',
                encoding="utf-8",
            )

            def run(*args):
                result = subprocess.run(args, cwd=root, capture_output=True, text=True, timeout=120)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                return result.stdout

            run(dotnet, "build", "-c", "Release", "-o", "unpatched", "-nologo", "-v", "q",
                "-nodeReuse:false", f"-p:RestoreConfigFile={root / 'NuGet.Config'}")
            baseline = run(dotnet, "unpatched/LoopbackBans.dll", "--unpatched")
            self.assertIn("REPRODUCED", baseline)
            run("git", "apply", str(HERE / "patches/0006-loopback-ban-exemption.patch"))
            # Keep generated output outside the source glob when building the patched version.
            run(dotnet, "build", "-c", "Release", "-o", "patched", "-nologo", "-v", "q",
                "-nodeReuse:false", f"-p:RestoreConfigFile={root / 'NuGet.Config'}")
            fixed = run(dotnet, "patched/LoopbackBans.dll")
            self.assertIn("PASS", fixed)
            print(baseline.strip())
            print(fixed.strip())


if __name__ == "__main__":
    unittest.main()
