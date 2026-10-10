"""The headless owner patch's guard, run for real (SF2, MUO patch).

patches/0001 is applied to the pinned AccountPrompt.cs, compiled with small
stand-ins for the ModernUO types it touches (tests/account_prompt) and run on
made-up accounts. Checks: a blank or published default password makes and
raises nothing; an existing account below owner (or game master) is raised
only if it already holds the configured password; one already at that level
takes the configured password (SF1). Skipped without dotnet or a ModernUO
checkout holding the pin (run launchers\\shard\\fetch.bat).

    python tools/modernuo/test_account_prompt.py
"""

from __future__ import annotations

import os
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

from test_upstream import HERE, shard_ref, shard_src

PATCH = HERE / "patches" / "0001-headless-owner-account.patch"
HARNESS = HERE / "tests" / "account_prompt"
TARGET = "Projects/UOContent/Misc/AccountPrompt.cs"
ENV_KEYS = ("UO_SHARD_OWNER", "UO_SHARD_OWNER_PASSWORD", "UO_SHARD_GM_ACCOUNTS", "UO_SHARD_GM_PASSWORD")


class AccountPromptGuardTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        dotnet = shutil.which("dotnet")
        src = shard_src()
        if not dotnet:
            raise unittest.SkipTest("no dotnet")
        if src is None:
            raise unittest.SkipTest(f"no ModernUO checkout holding {shard_ref()[:9]} (run launchers\\shard\\fetch.bat)")
        cls._tmp = tempfile.TemporaryDirectory(prefix="guo-sf2-")
        tmp = Path(cls._tmp.name)
        original = subprocess.run(
            ["git", "-C", str(src), "show", f"{shard_ref()}:{TARGET}"], capture_output=True, check=True
        ).stdout
        (tmp / TARGET).parent.mkdir(parents=True)
        (tmp / TARGET).write_bytes(original)
        applied = subprocess.run(["git", "apply", str(PATCH)], cwd=tmp, capture_output=True, text=True)
        if applied.returncode:
            raise AssertionError(f"0001 does not apply to the pin: {applied.stderr}")
        proj = tmp / "harness"
        proj.mkdir()
        shutil.copy(tmp / TARGET, proj / "AccountPrompt.cs")
        for name in ("Stubs.cs", "Program.cs"):
            shutil.copy(HARNESS / name, proj / name)
        shutil.copy(HARNESS / "AccountPromptHarness.csproj.txt", proj / "AccountPromptHarness.csproj")
        built = subprocess.run(
            [dotnet, "build", str(proj), "-c", "Release", "-o", str(tmp / "out"), "-nologo", "-v", "q", "-nodeReuse:false"],
            capture_output=True,
            text=True,
            env=dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1"),
        )
        if built.returncode:
            raise AssertionError(f"the patched AccountPrompt.cs does not build:\n{built.stdout}\n{built.stderr}")
        cls.dotnet = dotnet
        cls.dll = tmp / "out" / "AccountPromptHarness.dll"

    @classmethod
    def tearDownClass(cls):
        cls._tmp.cleanup()

    def boot(self, accounts: list[str], **env: str) -> tuple[dict[str, str], str]:
        """One headless boot; returns {name: "Level password=... chars=..."} and the log."""
        clean = {k: v for k, v in os.environ.items() if k not in ENV_KEYS}
        r = subprocess.run(
            [self.dotnet, str(self.dll), *accounts], capture_output=True, text=True, env={**clean, **env}, check=True
        )
        state = {}
        for line in r.stdout.splitlines():
            if line.startswith("account "):
                _, name, rest = line.split(" ", 2)
                state[name] = rest
        return state, r.stdout

    # ---- the owner

    def test_blank_password_makes_no_owner(self):
        state, log = self.boot([], UO_SHARD_OWNER="boss", UO_SHARD_OWNER_PASSWORD="  ")
        self.assertEqual(state, {})
        self.assertIn("is not set, so there is no owner account", log)

    def test_published_defaults_make_no_owner(self):
        for pw in ("guoprobe", "GuoProbe", "boss"):
            with self.subTest(password=pw):
                state, log = self.boot([], UO_SHARD_OWNER="boss", UO_SHARD_OWNER_PASSWORD=pw)
                self.assertEqual(state, {})
                self.assertIn("published default", log)

    def test_published_default_raises_no_existing_account(self):
        state, _ = self.boot(["boss:guoprobe:Player"], UO_SHARD_OWNER="boss", UO_SHARD_OWNER_PASSWORD="guoprobe")
        self.assertTrue(state["boss"].startswith("Player "))

    def test_fresh_owner_is_made(self):
        state, _ = self.boot([], UO_SHARD_OWNER="boss", UO_SHARD_OWNER_PASSWORD="s3cret-gen")
        self.assertEqual(state["boss"], "Owner password=s3cret-gen chars= protected")

    def test_stranger_holding_the_owner_name_stays_a_player(self):
        state, log = self.boot(["boss:strangers-pw:Player"], UO_SHARD_OWNER="boss", UO_SHARD_OWNER_PASSWORD="s3cret-gen")
        self.assertEqual(state["boss"], "Player password=strangers-pw chars=Player")
        self.assertIn("does not hold UO_SHARD_OWNER_PASSWORD; it is left as it is", log)
        self.assertNotIn("s3cret-gen", log)  # never logged (and not the account's)

    def test_player_holding_the_owner_password_is_raised(self):
        state, _ = self.boot(["boss:s3cret-gen:Player"], UO_SHARD_OWNER="boss", UO_SHARD_OWNER_PASSWORD="s3cret-gen")
        self.assertEqual(state["boss"], "Owner password=s3cret-gen chars=Owner protected")

    def test_existing_owner_takes_the_configured_password(self):
        # A shard made before SF1 has the owner at the old default; the new password replaces it.
        state, log = self.boot(["boss:guoprobe:Owner"], UO_SHARD_OWNER="boss", UO_SHARD_OWNER_PASSWORD="s3cret-gen")
        self.assertEqual(state["boss"], "Owner password=s3cret-gen chars=Owner protected")
        self.assertNotIn("s3cret-gen", log.split("account boss", 1)[1].split("\n", 1)[1])

    # ---- the game masters

    def test_gm_accounts_are_made_with_the_gm_password(self):
        state, _ = self.boot([], UO_SHARD_GM_ACCOUNTS="gm1,gm2", UO_SHARD_GM_PASSWORD="gm-gen")
        self.assertEqual(state["gm1"], "GameMaster password=gm-gen chars=")
        self.assertEqual(state["gm2"], "GameMaster password=gm-gen chars=")

    def test_gm_published_defaults_make_nothing(self):
        state, _ = self.boot([], UO_SHARD_GM_ACCOUNTS="gm1", UO_SHARD_GM_PASSWORD="guoprobe")
        self.assertEqual(state, {})
        state, log = self.boot([], UO_SHARD_GM_ACCOUNTS="gm1,gm2", UO_SHARD_GM_PASSWORD="gm1")
        self.assertEqual(list(state), ["gm2"])
        self.assertIn("is the name of 'gm1'", log)

    def test_gm_blank_password_makes_nothing(self):
        state, _ = self.boot([], UO_SHARD_GM_ACCOUNTS="gm1", UO_SHARD_GM_PASSWORD="")
        self.assertEqual(state, {})

    def test_stranger_holding_a_gm_name_stays_a_player(self):
        state, log = self.boot(["gm1:theirs:Player"], UO_SHARD_GM_ACCOUNTS="gm1", UO_SHARD_GM_PASSWORD="gm-gen")
        self.assertEqual(state["gm1"], "Player password=theirs chars=Player")
        self.assertIn("does not hold UO_SHARD_GM_PASSWORD", log)

    def test_existing_gm_takes_the_configured_password(self):
        # The pre-SF1 rule made each GM's password its own name.
        state, _ = self.boot(["gm1:gm1:GameMaster"], UO_SHARD_GM_ACCOUNTS="gm1", UO_SHARD_GM_PASSWORD="gm-gen")
        self.assertEqual(state["gm1"], "GameMaster password=gm-gen chars=GameMaster")


if __name__ == "__main__":
    unittest.main()
