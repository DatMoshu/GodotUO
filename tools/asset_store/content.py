"""V2 content envelope validation; installation remains inert."""
from pathlib import PurePosixPath
import re

try:
    from . import mods
except ImportError:
    import mods

CONTENT_TYPES = frozenset("static land texmap tiledata gump animation wearable hue sound music effect light translation font map region multi decoration item crafting loot vendor creature spawner encounter quest dialogue presentation authoring script hd-art splat voice mod water".split())

PACK_FIELDS = {"scale", "native_w", "native_h", "asset", "graphic", "body", "name", "wav"}


def validate_media(c, files, require):
    """Manifest metadata only; no payload decode or activation during install."""
    kind = c["type"]
    allowed = {"hd-art": {"scale", "native_w", "native_h", "asset", "graphic"},
               "splat": {"graphic"}, "voice": {"body", "name", "wav"}}.get(kind, set())
    require(all(c.get(key) is None for key in PACK_FIELDS - allowed), "media field on wrong component type")
    if kind not in {"hd-art", "splat", "voice"}:
        return
    require(c["target"] == "client", "media component must target client")
    extension = PurePosixPath(c["entry"]).suffix.lower()
    if kind in {"hd-art", "splat"}:
        require(type(c.get("graphic")) is int and 0 <= c["graphic"] <= 65535, "invalid graphic binding")
    if kind == "hd-art":
        require(extension == ".png", "hd-art entry must be PNG")
        require(c.get("asset") in {"static", "land"}, "invalid hd-art asset namespace")
        require(type(c.get("scale")) in {int, float} and 1 <= c["scale"] <= 16, "invalid hd-art scale")
        for key in ("native_w", "native_h"):
            require(type(c.get(key)) is int and 1 <= c[key] <= 4096, "invalid hd-art native dimensions")
        require(c["asset"] != "land" or c["native_w"] == c["native_h"], "hd-art land must be square")
    elif kind == "splat":
        require(extension == ".ply", "splat entry must be PLY")
    else:
        require(extension == ".json", "voice entry must be JSON")
        body, name = c.get("body"), c.get("name")
        require((body is not None) != (name is not None), "voice needs one NPC body or name binding")
        if body is not None:
            require(type(body) is int and 0 <= body <= 65535, "invalid voice body binding")
        else:
            require(isinstance(name, str) and name.strip() and len(name.encode("utf-16-le")) // 2 <= 100
                    and not any(ord(ch) < 32 or ord(ch) == 127 for ch in name), "invalid voice name binding")
        wav = c.get("wav")
        require(isinstance(wav, list) and 0 < len(wav) <= 512, "voice needs WAV entries")
        for path in wav:
            require(isinstance(path, str) and path in files and PurePosixPath(path).suffix.lower() == ".wav", "undeclared voice WAV entry")
        require(len(set(wav)) == len(wav), "duplicate voice WAV entry")


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
        require(not (set(c) - ({"id", "type", "target", "entry", "references", "language", "runtime", "runtime_version", "capabilities"} | PACK_FIELDS | mods.MOD_FIELDS)), "unknown component field")
        identifier(c.get("id"))
        require(c["id"] not in identities, "duplicate component id")
        identities.add(c["id"])
        require(c.get("type") in CONTENT_TYPES, "unsupported content type")
        require(c.get("target") in ("client", "server", "shared"), "invalid component target")
        require(target == "combined" or c["target"] in (target, "shared"), "component target mismatch")
        targets.add(c["target"])
        require(isinstance(c.get("entry"), str) and c["entry"] in m["files"], "undeclared component entry")
        require(PurePosixPath(c["entry"]).suffix.lower() in (".json", ".png", ".wav", ".ogg", ".razor", ".ply"), "invalid content entry type")
        validate_media(c, m["files"], require)
        if c["type"] == "water":
            # A style (ADR-0037 decision 6): data for a built-in model, read by the client only.
            require(c["target"] == "client", "water component must target client")
            require(PurePosixPath(c["entry"]).suffix.lower() == ".json", "water entry must be JSON")
        mods.validate_component(c, m, require)
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
    mods.validate_assemblies(m, require)
    require(target != "combined" or "shared" in targets or {"client", "server"} <= targets, "combined pack missing target")
    ply = {c["entry"] for c in components if c["type"] == "splat"}
    require(all(PurePosixPath(path).suffix.lower() != ".ply" or path in ply for path in m["files"]), "PLY payload requires splat component")
    require(all(PurePosixPath(c["entry"]).suffix.lower() != ".ply" or c["type"] == "splat" for c in components), "PLY entry requires splat component")
    for c in components:
        for ref in c.get("references") or []:
            pack, local = ref.split(":")
            require(pack != m["id"] or local in identities, "missing local component reference")
