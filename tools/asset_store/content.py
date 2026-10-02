"""V2 content envelope validation; installation remains inert."""
from pathlib import PurePosixPath
import re

CONTENT_TYPES = frozenset("static land texmap tiledata gump animation wearable hue sound music effect light translation font map region multi decoration item crafting loot vendor creature spawner encounter quest dialogue presentation authoring script".split())


def validate_content(m, require, identifier, version):
    require(not (set(m) - {"schema", "id", "version", "kind", "target", "title", "author", "licence", "min_profile_version", "preview", "files", "dependencies", "components", "url", "sha256", "size", "preview_url"}), "unknown content manifest field")
    require(m.get("kind") == "content", "v2 requires content kind")
    target = m.get("target")
    require(target in ("client", "server", "combined"), "invalid content target")
    deps = m.get("dependencies")
    require(isinstance(deps, dict) and len(deps) <= 128, "invalid dependencies")
    for key, value in deps.items():
        identifier(key)
        version(value)
        require(key != m["id"], "self dependency")
    components = m.get("components")
    require(isinstance(components, list) and 0 < len(components) <= 512, "invalid components")
    identities = set()
    targets = set()
    for c in components:
        require(isinstance(c, dict), "invalid component")
        require(not (set(c) - {"id", "type", "target", "entry", "references", "language", "runtime", "runtime_version", "capabilities"}), "unknown component field")
        identifier(c.get("id"))
        require(c["id"] not in identities, "duplicate component id")
        identities.add(c["id"])
        require(c.get("type") in CONTENT_TYPES, "unsupported content type")
        require(c.get("target") in ("client", "server", "shared"), "invalid component target")
        require(target == "combined" or c["target"] in (target, "shared"), "component target mismatch")
        targets.add(c["target"])
        require(isinstance(c.get("entry"), str) and c["entry"] in m["files"], "undeclared component entry")
        require(PurePosixPath(c["entry"]).suffix.lower() in (".json", ".png", ".wav", ".ogg", ".razor"), "invalid content entry type")
        refs = c.get("references") if c.get("references") is not None else []
        require(isinstance(refs, list) and len(refs) <= 512, "invalid references")
        for ref in refs:
            require(isinstance(ref, str) and ref.count(":") == 1, "invalid content reference")
            pack, local = ref.split(":")
            identifier(pack)
            identifier(local)
            require(pack == m["id"] or pack in deps, "undeclared reference dependency")
        if c["type"] == "script":
            require(c.get("language") == "razor-ce" and c.get("runtime") == "guo-razor", "unsupported script runtime")
            version(c.get("runtime_version"))
            caps = c.get("capabilities")
            require(isinstance(caps, list) and len(caps) <= 64, "invalid script capabilities")
            for cap in caps:
                require(isinstance(cap, str) and re.fullmatch(r"[a-z][a-z0-9]*(\.[a-z][a-z0-9]*)*", cap) and len(cap) <= 100, "invalid script capability")
            require(len(set(caps)) == len(caps), "duplicate script capability")
    require(target != "combined" or "shared" in targets or {"client", "server"} <= targets, "combined pack missing target")
    for c in components:
        for ref in c.get("references") or []:
            pack, local = ref.split(":")
            require(pack != m["id"] or local in identities, "missing local component reference")
