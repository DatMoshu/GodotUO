#!/usr/bin/env python3
"""UI-format ComfyUI workflow -> API-format prompt, with subgraph expansion.

ComfyUI's frontend saves UI format (nodes/links/groups + definitions.subgraphs);
its /prompt endpoint needs API format ({node_id: {class_type, inputs}}).
'Save (API Format)' in the frontend does this conversion; this module does the
same so hand-crafted workflows (MultisMaker1, FASTAUDIOTGEN) queue unchanged.

Rules (matching the frontend):
- A linked input becomes [src_id_str, src_slot]; the link wins over any widget.
- An unlinked input with a widget marker takes the next widgets_values entry.
- widgets_values entries for linked inputs exist but are stale: consumed and discarded.
- Subgraph nodes (type == definitions.subgraphs[].id) are inlined: inner node/link
  ids are remapped to fresh ids, boundary links from inputNode (-10) take the
  outer feed (link or widget literal), links into outputNode (-20) fan out to the
  outer consumers. Expansion repeats until no subgraph node remains.
- Only sink nodes (no outputs) may be dropped via drop_types.

Link records come in two shapes: UI lists [id, src, src_slot, dst, dst_slot, type]
and dicts {id, origin_id, origin_slot, target_id, target_slot}.
"""

from __future__ import annotations

import copy
import json
from pathlib import Path

INPUT_NODE = -10
OUTPUT_NODE = -20


def _norm_link(raw) -> tuple[int, int, int, int, int]:
    if isinstance(raw, dict):
        return (raw["id"], raw["origin_id"], raw["origin_slot"], raw["target_id"], raw["target_slot"])
    return (raw[0], raw[1], raw[2], raw[3], raw[4])


def _is_subgraph_node(node: dict, subgraphs: dict) -> bool:
    return node.get("type") in subgraphs


def _widget_inputs(node: dict) -> list[dict]:
    return [i for i in (node.get("inputs") or []) if isinstance(i, dict) and "widget" in i]


def _outer_feed(node: dict, slot: int):
    """(linked_link_id_or_None, widget_value_or_None) for outer input slot.

    Widget cursor advances over widget-marked inputs in order; a linked widget
    input's value is stale and discarded.
    """
    widgets = node.get("widgets_values") or []
    cursor = 0
    for idx, inp in enumerate(node.get("inputs") or []):
        if not isinstance(inp, dict) or "widget" not in inp:
            if idx == slot:
                return (inp.get("link"), None) if isinstance(inp, dict) else (None, None)
            continue
        val = widgets[cursor] if cursor < len(widgets) else None
        if idx == slot:
            return (inp.get("link"), None) if inp.get("link") is not None else (None, val)
        cursor += 1
    return (None, None)


def expand_subgraphs(ui: dict) -> tuple[dict, dict]:
    """Returns (nodes_by_id, links_by_id) with no subgraph nodes left."""
    subgraphs = {s["id"]: s for s in (ui.get("definitions", {}) or {}).get("subgraphs", [])}
    nodes = {n["id"]: copy.deepcopy(n) for n in ui.get("nodes", [])}
    links: dict[int, tuple] = {}
    for raw in ui.get("links", []) or []:
        lid, src, sslot, dst, dslot = _norm_link(raw)
        links[lid] = (src, sslot, dst, dslot)
    if not subgraphs:
        resolve_reroutes(nodes, links)
        return nodes, links
    used = set(nodes) | set(links)
    for n in nodes.values():
        for inp in n.get("inputs") or []:
            if isinstance(inp, dict) and isinstance(inp.get("link"), int):
                used.add(inp["link"])
    counter = [max(used, default=0) + 1]

    def fresh() -> int:
        v = counter[0]
        counter[0] += 1
        return v

    for _ in range(10):  # nesting depth guard; real workflows use 1
        if not _expand_one(subgraphs, nodes, links, fresh):
            break
    else:
        raise RuntimeError("subgraph expansion did not converge (nesting too deep?)")
    for nid, node in nodes.items():
        if _is_subgraph_node(node, subgraphs):
            raise RuntimeError(f"unexpanded subgraph node {nid} type {node.get('type')}")
    resolve_reroutes(nodes, links)
    return nodes, links


def _expand_one(subgraphs, nodes, links, fresh) -> bool:
    # Same as _expand_once but with injectable id source.
    for nid, node in list(nodes.items()):
        if node.get("type") not in subgraphs:
            continue
        sg = subgraphs[node["type"]]
        sg_inputs = sg.get("inputs", []) or []
        idmap = {}
        for inner in sg.get("nodes", []) or []:
            idmap[inner["id"]] = fresh()
        new_nodes = {}
        for inner in sg.get("nodes", []) or []:
            cp = copy.deepcopy(inner)
            cp["id"] = idmap[inner["id"]]
            new_nodes[cp["id"]] = cp
        linkmap = {}
        for raw in (sg.get("links", []) or []):
            lid, *_ = _norm_link(raw)
            linkmap[lid] = fresh()
        for cp in new_nodes.values():
            for inp in cp.get("inputs") or []:
                if isinstance(inp, dict) and inp.get("link") in linkmap:
                    inp["link"] = linkmap[inp["link"]]
            for out in cp.get("outputs") or []:
                if isinstance(out, dict) and out.get("links"):
                    out["links"] = [linkmap.get(l, l) for l in out["links"]]
        new_links: dict[int, tuple] = {}
        by_slot: dict[int, list] = {}
        for raw in (sg.get("links", []) or []):
            lid, src, sslot, dst, dslot = _norm_link(raw)
            if src == INPUT_NODE:
                by_slot.setdefault(sslot, []).append((lid, dst, dslot))
            elif dst != OUTPUT_NODE:
                new_links[linkmap[lid]] = (idmap[src], sslot, idmap[dst], dslot)
        for j in range(len(sg_inputs)):
            outer_link, literal = _outer_feed(node, j)
            for (lid, dst, dslot) in by_slot.get(j, []):
                if outer_link is not None:
                    if outer_link not in links:
                        raise KeyError(f"outer link {outer_link} missing for subgraph input {j}")
                    (osrc, ossl, _, _) = links[outer_link]
                    new_links[linkmap[lid]] = (osrc, ossl, idmap[dst], dslot)
                else:
                    inner_node = new_nodes[idmap[dst]]
                    for inp in inner_node.get("inputs") or []:
                        if isinstance(inp, dict) and inp.get("link") == linkmap[lid]:
                            inp["link"] = None
                            inp["literal"] = literal
                            break
        out_links: dict[int, list] = {}
        for raw in (sg.get("links", []) or []):
            lid, src, sslot, dst, dslot = _norm_link(raw)
            if dst == OUTPUT_NODE:
                out_links.setdefault(dslot, []).append((lid, src, sslot))
        for j, out in enumerate(node.get("outputs") or []):
            for outer_lid in (out.get("links") or []):
                if outer_lid not in links:
                    continue
                (_, _, odst, odslot) = links[outer_lid]
                for (lid, src, sslot) in out_links.get(j, []):
                    new_lid = fresh()
                    new_links[new_lid] = (idmap[src], sslot, odst, odslot)
                    # Point the outer consumer at the replacement link.
                    consumer = nodes.get(odst)
                    if consumer is not None:
                        for inp in consumer.get("inputs") or []:
                            if isinstance(inp, dict) and inp.get("link") == outer_lid:
                                inp["link"] = new_lid
                del links[outer_lid]
        for inp in node.get("inputs") or []:
            if isinstance(inp, dict) and inp.get("link") in links:
                del links[inp["link"]]
        for k, v in new_links.items():
            links[k] = v
        nodes.update(new_nodes)
        del nodes[nid]
        return True
    return False


def resolve_reroutes(nodes: dict, links: dict) -> None:
    """Splice out frontend Reroute nodes: consumers are rewired to the true source."""
    for nid, node in list(nodes.items()):
        if node.get("type") != "Reroute":
            continue
        ins = [i for i in (node.get("inputs") or []) if isinstance(i, dict) and i.get("link") in links]
        if len(ins) != 1:
            raise ValueError(f"reroute {nid}: expected 1 input link, found {len(ins)}")
        (src, sslot, _, _) = links[ins[0]["link"]]
        for out in node.get("outputs") or []:
            for outer_lid in (out.get("links") or []) if isinstance(out, dict) else []:
                if outer_lid not in links:
                    continue
                (_, _, odst, odslot) = links[outer_lid]
                links[outer_lid] = (src, sslot, odst, odslot)
        del links[ins[0]["link"]]
        del nodes[nid]


def apply_modes(nodes: dict, links: dict) -> None:
    """Mute (mode 2) drops the node; bypass (mode 4) passes its input through.

    Matches the frontend: muted nodes are omitted from the prompt, bypassed
    nodes are replaced by a direct link from their first linked input.
    """
    for nid, node in list(nodes.items()):
        mode = node.get("mode", 0)
        if mode == 0:
            continue
        if mode == 2:
            for inp in node.get("inputs") or []:
                if isinstance(inp, dict) and inp.get("link") in links:
                    del links[inp["link"]]
            for out in node.get("outputs") or []:
                for lid in (out.get("links") or []) if isinstance(out, dict) else []:
                    links.pop(lid, None)
            del nodes[nid]
        elif mode == 4:
            srcs = [(inp.get("link"), links[inp["link"]]) for inp in (node.get("inputs") or [])
                    if isinstance(inp, dict) and inp.get("link") in links]
            if not srcs:
                raise ValueError(f"bypassed node {nid} ({node.get('type')}) has no linked input")
            (src, sslot, _, _) = srcs[0][1]
            for out in node.get("outputs") or []:
                for lid in (out.get("links") or []) if isinstance(out, dict) else []:
                    if lid not in links:
                        continue
                    (_, _, odst, odslot) = links[lid]
                    links[lid] = (src, sslot, odst, odslot)
            for inp in node.get("inputs") or []:
                if isinstance(inp, dict) and inp.get("link") in links:
                    del links[inp["link"]]
            del nodes[nid]
        else:
            raise ValueError(f"node {nid} ({node.get('type')}) has mode {mode}; unbypass it first")


def to_api_prompt(nodes: dict, links: dict, drop_types: tuple = ()) -> dict:
    """Flat nodes/links -> API prompt. Drops sink nodes of the given types."""
    nodes = dict(nodes)
    links = dict(links)
    apply_modes(nodes, links)
    for nid, node in list(nodes.items()):
        if node.get("type") in drop_types:
            outgoing = [l for o in (node.get("outputs") or []) if isinstance(o, dict)
                        for l in (o.get("links") or []) if l in links]
            if outgoing:
                raise ValueError(f"refusing to drop non-sink node {nid} ({node.get('type')})")
            for inp in node.get("inputs") or []:
                if isinstance(inp, dict) and inp.get("link") in links:
                    del links[inp["link"]]
            del nodes[nid]
    # API links only need the source: {link_id: (src_id, src_slot)}.
    link_src = {lid: (src, sslot) for lid, (src, sslot, _dst, _dslot) in links.items()}
    prompt = {}
    for nid in sorted(nodes):
        node = nodes[nid]
        widgets = node.get("widgets_values") or []
        cursor = 0
        inputs: dict = {}
        for inp in node.get("inputs") or []:
            if not isinstance(inp, dict):
                continue
            name = inp.get("name")
            if inp.get("link") is not None:
                if inp.get("link") not in link_src:
                    raise KeyError(f"node {nid} input '{name}' references missing link {inp.get('link')}")
                (src, sslot) = link_src[inp["link"]]
                inputs[name] = [str(src), sslot]
                if "widget" in inp:
                    cursor = _consume_widget(widgets, cursor, name)  # stale, discarded
            elif "literal" in inp:
                inputs[name] = inp["literal"]
                # The inner node's own widget value is stale: consume and discard it.
                if "widget" in inp and cursor < len(widgets):
                    cursor = _consume_widget(widgets, cursor, name)
            elif "widget" in inp:
                if cursor >= len(widgets):
                    # UI-only widgets (AUDIO_UI and the like) are marked but never
                    # serialized; the backend uses its default when they are absent.
                    continue
                inputs[name] = widgets[cursor]
                cursor = _consume_widget(widgets, cursor, name)
        prompt[str(nid)] = {"class_type": node.get("type"), "inputs": inputs}
    return prompt


# Values of the input-less control widget the frontend adds after every seed.
_SEED_CONTROLS = frozenset({"fixed", "increment", "decrement", "randomize"})


def _consume_widget(widgets: list, cursor: int, name: str | None = None) -> int:
    """Move past one consumed widget value, plus a trailing seed control widget.

    `seed`/`noise_seed` inputs carry an extra input-less widget
    (control_after_generate); it serializes with the rest and must be skipped
    or every input after it misaligns.
    """
    cursor += 1
    if name in ("seed", "noise_seed") and cursor < len(widgets) \
            and widgets[cursor] in _SEED_CONTROLS:
        cursor += 1
    return cursor


def convert_file(path: str | Path, drop_types: tuple = (), ui_set: dict | None = None) -> dict:
    """Load a workflow file (UI or API format) and return an API prompt."""
    raw = json.loads(Path(path).read_text(encoding="utf-8"))
    if isinstance(raw, dict) and "nodes" in raw:
        if ui_set:
            raw = copy.deepcopy(raw)
            apply_ui_set(raw, ui_set)
        nodes, links = expand_subgraphs(raw)
        return to_api_prompt(nodes, links, drop_types)
    # Already API format.
    return raw


def apply_ui_set(ui: dict, ui_set: dict) -> None:
    """Override outer-node widgets: {(node_id, widget_index): value}."""
    nodes = {n["id"]: n for n in ui.get("nodes", [])}
    for (nid, widx), val in ui_set.items():
        node = nodes.get(nid)
        if node is None:
            raise KeyError(f"ui_set: no node {nid}")
        widgets = node.get("widgets_values") or []
        if not (0 <= widx < len(widgets)):
            raise KeyError(f"ui_set: node {nid} has {len(widgets)} widgets, no index {widx}")
        widgets[widx] = val
        node["widgets_values"] = widgets
