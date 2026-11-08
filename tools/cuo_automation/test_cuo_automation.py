"""Tests that need no shard and no ClassicUO build: the tool catalogue against GUO's, the injection, the scrub."""

from __future__ import annotations

import json
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
sys.path.insert(0, str(HERE))

import run  # noqa: E402

BS = chr(92)
CHUNK = r'(?:[^"' + BS * 2 + r']|' + BS * 2 + r'.)*'
TOOL = re.compile(r'Tool\("(?P<name>guo_\w+)",\s*"(?P<desc>' + CHUNK + r')",\s*"(?P<schema>' + CHUNK + r')"\)')


def catalogue(path: Path) -> dict[str, dict]:
    tools = {}
    for m in TOOL.finditer(path.read_text(encoding="utf-8")):
        schema = m.group("schema").replace('\\"', '"').replace("\\\\", "\\")
        tools[m.group("name")] = json.loads(schema)
    return tools


def test_shared_tools_have_guos_argument_shapes():
    guo = catalogue(ROOT / "godot" / "GUO" / "src" / "Automation" / "McpHost.cs")
    cuo = catalogue(HERE / "CuoMcpHost.cs")
    assert set(cuo) == {"guo_ui", "guo_input", "guo_wait", "guo_state", "guo_quit", "guo_screenshot"}
    for name, schema in cuo.items():
        assert schema == guo[name], name


def test_guo_overlay_is_the_only_gap():
    guo = catalogue(ROOT / "godot" / "GUO" / "src" / "Automation" / "McpHost.cs")
    cuo = catalogue(HERE / "CuoMcpHost.cs")
    assert set(guo) - set(cuo) == {"guo_overlay"}


def test_the_scenario_runner_tools_are_all_served():
    sys.path.insert(0, str(ROOT / "tools" / "scenario_run"))
    import driver

    assert set(driver.GAME_TOOLS.values()) <= set(catalogue(HERE / "CuoMcpHost.cs"))


def test_the_server_is_off_without_both_variables_and_guards_like_guo():
    source = (HERE / "CuoMcpHost.cs").read_text(encoding="utf-8")
    start = source[source.index("internal static void Start()"):]
    start = start[:start.index("try")]
    assert 'GUO_MCP_PORT' in start and 'GUO_MCP_TOKEN' in start
    assert "IsNullOrEmpty(portText)" in start and "token.Length < 32" in start
    assert "port < 1024 || port > 65535" in start
    assert "IPAddress.Loopback" in source and "IPAddress.Any" not in source


def test_it_is_compiled_only_into_classicuo():
    targets = (ROOT / "tools" / "render_dump" / "inject.targets").read_text(encoding="utf-8")
    assert "cuo_automation" in targets and "CuoMcpHost.cs" in targets
    # Never in the Godot project, so never in an export.
    for csproj in (ROOT / "godot" / "GUO").glob("*.csproj"):
        assert "cuo_automation" not in csproj.read_text(encoding="utf-8")
    assert not list((ROOT / "godot" / "GUO").rglob("CuoMcpHost.cs"))


def test_scrub_takes_the_password_and_token_out_of_the_log(tmp_path):
    log = tmp_path / "cuo.log"
    log.write_text("a | ARG: username, VALUE: owner\na | ARG: password, VALUE: hunter2hunter2\nb hunter2hunter2 tok123\n", encoding="utf-8")
    run.scrub(log, ["hunter2hunter2", "tok123"])
    text = log.read_text(encoding="utf-8")
    assert "hunter2" not in text and "tok123" not in text
    assert "ARG: username, VALUE: owner" in text


def test_free_port_is_a_usable_loopback_port():
    port = run.free_port()
    assert 1024 <= port <= 65535
