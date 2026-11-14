"""Scoring math of tools/competition_bench/run.py: penalty curve, noise gate, validity, DQ and ordering rules."""
import numpy as np
import pytest
from PIL import Image

import run


@pytest.mark.parametrize("added,expected", [(-1.0, 0.0), (0.0, 0.0), (2.5, 0.0), (2.75, 1.6666666666666667),
                                            (3.25, 5.0), (4.0, 10.0)])
def test_penalty_curve(added, expected):
    assert run.penalty(added) == pytest.approx(expected)


def test_above_four_is_dq():
    assert run.penalty(4.0001) is None


def test_noise_gate():
    assert run.noise_gate([5.0, 5.2, 5.1])[1] is False      # 0.2 <= 0.3
    assert run.noise_gate([5.0, 5.4, 5.1])[1] is False      # 0.4 <= 10% of 5.1
    assert run.noise_gate([5.0, 6.0, 5.1])[1] is True       # 1.0 > 0.51
    assert run.noise_gate([1.0, 1.4, 1.1])[1] is True       # 0.4 > max(0.3, 0.11)


def test_single_repeat_noise_is_unknown():
    assert run.noise_gate([5.0]) == (None, None)


def row(median, unstable=False, p95=None):
    return {"median_gpu_ms": median, "p95_gpu_ms": p95, "p99_gpu_ms": p95, "noise_ms": 0.0, "unstable": unstable}


def test_score_max_added_and_dq():
    base = {"a|noon": row(5.0), "a|dusk": row(6.0)}
    ok = run.score({"a|noon": row(6.0), "a|dusk": row(8.0)}, base)       # +1.0, +2.0
    assert ok["max_added_ms"] == pytest.approx(2.0) and ok["penalty_points"] == 0.0 and ok["status"] == "scored"
    mid = run.score({"a|noon": row(8.0), "a|dusk": row(6.0)}, base)      # +3.0
    assert mid["penalty_points"] == pytest.approx(3.3333333)
    dq = run.score({"a|noon": row(9.5), "a|dusk": row(6.0)}, base)       # +4.5
    assert dq["disqualified"] and dq["status"] == "DQ" and not dq["provisional"]


def test_provisional_uses_only_the_worst_key():
    base = {"a|noon": row(5.0), "a|dusk": row(6.0)}
    noisy_other = run.score({"a|noon": row(9.5), "a|dusk": row(6.0, unstable=True)}, base)  # worst key is stable
    assert noisy_other["status"] == "DQ" and not noisy_other["provisional"]
    noisy_worst = run.score({"a|noon": row(9.5, unstable=True), "a|dusk": row(6.0)}, base)
    assert noisy_worst["provisional"] and noisy_worst["status"].startswith("PROVISIONAL")


def test_single_repeat_refuses_to_certify():
    base = {"a|noon": row(5.0)}
    entry = {"a|noon": {**row(9.5), "noise_ms": None, "unstable": None}}
    assert run.score(entry, base)["status"].startswith("UNCERTIFIED")


def test_invalid_when_a_planned_key_is_missing_or_process_failed_or_env_differs():
    base = {"a|noon": row(5.0), "a|dusk": row(6.0)}
    assert run.score({"a|noon": row(6.0)}, base, ["a|noon", "a|dusk"])["status"] == "INVALID"
    assert run.score({"a|noon": row(6.0)}, base, ["a|noon"], processes_ok=False)["status"] == "INVALID"
    entry = {"a|noon": row(6.0)}
    got = run.score(entry, base, ["a|noon"], {"adapter": "X", "driver": "vulkan", "method": "forward_plus", "build": "Debug GuoLabs"},
                    {"adapter": "Y", "driver": "vulkan", "method": "forward_plus", "build": "Debug GuoLabs"})
    assert got["status"] == "INVALID" and "adapter" in got["invalid_reasons"][0]
    assert run.score({"a|noon": {"median_gpu_ms": None}}, base, ["a|noon"])["status"] == "INVALID"


def test_summarize_medians_and_noise():
    bench = {"repeatResults": [{"gpu_ms": {"count": 9, "median": m, "p95": m + 1, "p99": m + 2},
                                "frame_ms": {"count": 9, "median": 3.0}, "cpu_ms": {"count": 9, "median": 1.0}}
                               for m in (5.0, 5.1, 5.6)]}
    got = run.summarize([bench])
    assert got["median_gpu_ms"] == pytest.approx(5.1) and got["noise_ms"] == pytest.approx(0.6) and got["unstable"]


def test_plan_is_seeded_and_latin_over_builds():
    cells = [("s", "noon", "v"), ("s", "dusk", "v"), ("t", "noon", "v")]
    a = run.plan_order(["x", "y", "z"], cells, 3, 7)
    assert a == run.plan_order(["x", "y", "z"], cells, 3, 7) and a != run.plan_order(["x", "y", "z"], cells, 3, 8)
    for rnd in a:
        assert len(rnd) == 9 and {cell[1:] for cell in rnd} == set(cells)
    first = [{r[0] for r in rnd[::3]} for rnd in a]  # builds that lead a cell cycle across the rounds
    assert all(len(f) >= 1 for f in first)
    positions = {b: set() for b in "xyz"}
    for rnd in a:
        for i in range(0, 9, 3):
            for pos, r in enumerate(rnd[i:i + 3]):
                positions[r[0]].add(pos)
    assert all(len(p) == 3 for p in positions.values())


def test_scrubbed_env_keeps_allowlist_only(monkeypatch):
    monkeypatch.setenv("SECRET_TOKEN", "x"); monkeypatch.setenv("GUO_BENCH_FOO", "1"); monkeypatch.setenv("VK_ICD_FILENAMES", "evil")
    env = run.scrubbed_env({"UO_CLIENT_DATA": "d"})
    assert "SECRET_TOKEN" not in env and "VK_ICD_FILENAMES" not in env and env["GUO_BENCH_FOO"] == "1" and env["UO_CLIENT_DATA"] == "d"


def test_offparity_exact(tmp_path):
    a, b = tmp_path / "a", tmp_path / "b"; a.mkdir(); b.mkdir()
    img = np.zeros((4, 4, 4), np.uint8)
    Image.fromarray(img).save(a / "x-town-roofs-noon-classic.png"); Image.fromarray(img).save(b / "y-town-roofs-noon-classic.png")
    assert run.offparity(a, b) == 0
    img[1, 1, 0] = 1
    Image.fromarray(img).save(b / "y-town-roofs-noon-classic.png")
    assert run.offparity(a, b) == 1


def test_capture_results_summarise_without_timing_keys():
    results = [{"build": "a", "scene": "town-roofs", "time": "noon", "variant": "classic",
                "bench": {"status": "pass", "image_sha256": "ab", "still": "x.png"}}]
    assert run.group(results, "a", "classic")["town-roofs|noon"]["median_gpu_ms"] is None
    assert run.capture_group(results, "a", "classic")["town-roofs|noon"]["image_sha256"] == "ab"


def test_labs_check_names_build_command(tmp_path):
    with pytest.raises(SystemExit) as e:
        run.check_labs_build(tmp_path)
    assert "GuoLabs=true" in str(e.value)


def test_seam_lines_and_doc_section():
    assert run.seam_lines("a\n  CompetitionBenchClock.Active\nb\nBenchGpuMarkers.End();") == ["CompetitionBenchClock.Active", "BenchGpuMarkers.End();"]
    assert run.doc_section("x\n## 53. Competition benchmark results (y)\nbody").startswith("## N. Competition")


def test_provisional_when_another_key_could_exceed_the_worst():
    base = {"a|noon": row(5.0), "a|dusk": row(5.0)}
    entry = {"a|noon": row(9.0), "a|dusk": {**row(8.8), "noise_ms": 0.4}}   # 4.0 vs 3.8 + 0.4 > 4.0
    got = run.score(entry, base)
    assert got["provisional"] and got["contenders"] == ["a|dusk"]


def test_doc_section_stops_at_next_heading():
    text = "## 52. x\nold\n## 53. Competition benchmark results (a)\nbody\n## 54. other\nmore"
    assert run.doc_section(text) == "## N. Competition benchmark results (a)\nbody"
    assert run.doc_section(text) == run.doc_section(text.replace("## 53.", "## 54."))


def test_banned_api_scan_ignores_comments_and_finds_calls():
    assert run.banned_api_hits("// Engine.TimeScale in a comment\nprofile.X = 1;") == []
    assert "Engine." in run.banned_api_hits("Engine.TimeScale = 2;")
    assert "RenderingServer" in run.banned_api_hits("RenderingServer.X();")


def test_seam_blocks_compare_whole_blocks():
    a = "x\n#if GUO_LABS\n  CompetitionBenchClock.Active = 1;\n#endif\n"
    b = "x\n#if GUO_LABS\n  CompetitionBenchClock.Active = 2;\n#endif\n"
    assert run.seam_blocks(a) != run.seam_blocks(b) and run.seam_blocks(a) == run.seam_blocks(a.replace("  ", " "))


def test_structural_seam_regexes():
    good = "private void RenderThread()\n    {\n#if GUO_LABS\n        GUO.Labs.CompetitionBench.BenchGpuMarkers.PreDrawBegin(); // x\n#endif\n        long s;\n"
    assert run.BEGIN_RE.search(good) and not run.BEGIN_RE.search(good.replace("{\n#if", "{\n int a;\n#if"))
    end = "#if GUO_LABS\n GUO.Labs.CompetitionBench.BenchGpuMarkers.PreDrawEnd();\n#endif\n RecordTicks += 1;"
    assert run.END_RE.search(end) and not run.END_RE.search(end.replace("RecordTicks", "Other"))


def test_honest_near_zero_numbers_are_not_provisional():
    base = {"a|noon": row(5.0), "a|dusk": row(5.0)}
    entry = {"a|noon": {**row(5.1), "noise_ms": 0.1}, "a|dusk": {**row(5.05), "noise_ms": 0.2}}
    got = run.score(entry, base)
    assert not got["provisional"] and got["contenders"] == [] and got["status"] == "scored"


def test_outcome_changes_band_or_points():
    assert run.outcome_changes(4.0, 4.3) and run.outcome_changes(2.4, 2.6)      # crosses 4.0 / 2.5
    assert run.outcome_changes(3.0, 3.4) is True                                   # +2.7 points
    assert run.outcome_changes(3.0, 3.05) is False                                  # +0.33 points


def test_clock_drift_makes_provisional():
    base = {"a|noon": row(5.0)}
    entry = {"a|noon": {**row(5.5), "clock_drift": True}}
    assert run.score(entry, base)["provisional"]


def test_common_sha_and_expect_dll_rules():
    assert not run.valid_common_sha("abc123") and run.valid_common_sha("0123456789ab")
    builds = {"common": {"dll_sha256": "aa"}, "e1": {"dll_sha256": "bb"}}
    assert len(run.expect_problems(builds, None, False)) == 2 and run.expect_problems(builds, None, True) == []
    assert run.expect_problems(builds, ["common=aa", "e1=bb"], False) == []
    assert "not the coordinator rebuild" in run.expect_problems(builds, ["common=aa", "e1=zz"], False)[0]


def test_banned_scan_flags_bench_symbols_but_not_the_namespace():
    assert run.banned_api_hits("namespace GUO.Labs.CompetitionBench;\nclass X {}") == []
    assert "BenchGpuMarkers" in run.banned_api_hits("BenchGpuMarkers.Enabled = false;")
    assert "GUO_BENCH" in run.banned_api_hits('var x = "GUO_BENCH_OUT";')


def test_tail_regex_requires_nothing_after_record_ticks():
    ok = "RecordTicks += 1;\n            Volatile.Write(ref _pending, 0);\n        }\n    }\n"
    assert run.TAIL_RE.search(ok) and not run.TAIL_RE.search(ok.replace("Volatile.Write", "Other(); Volatile.Write"))


def test_budget_text_shows_frames_and_an_estimate_not_sim_seconds():
    text = run.budget_text(1200, 18000, 3, 20, 5, 3.0, 12)
    assert "57600 frames" in text and "3 ms/frame" in text and "estimate" in text and "/60 s" not in text
    assert "about 203 s wall" in text   # 20 + 2*5 + 57600*3/1000 = 202.8, rounded to 203
