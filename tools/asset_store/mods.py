"""The `mod` component kind (docs/asset_pack_ecosystem.md, Mod components; ADR-0038 amendment).

Mirrors src/Store/StoreMods.cs: the same manifest checks, the same signed message, the same
payload check against the mod's own mod.json. Publishers sign here; the client only verifies.
"""
from pathlib import PurePosixPath
import json
import re

try:
    from . import ed25519
except ImportError:  # run as a script from tools/asset_store
    import ed25519

ABI = 1
MOD_FIELDS = {"assembly", "abi", "packets", "gumps", "key", "signature"}
PACKET_FIELDS = {"id", "subcommand", "length", "override"}
RESERVED_SUBCOMMANDS = {0x4755}  # the shard offer, guo/shard-offer@1
SIGNATURE_SCHEMA = "guo/mod-signature@1"


def folder(path):
    slash = path.rfind("/")
    return "" if slash < 0 else path[:slash + 1]


def _number(value, maximum, require):
    if isinstance(value, str) and value.lower().startswith("0x") and re.fullmatch(r"[0-9a-fA-F]+", value[2:]):
        value = int(value[2:], 16)
    require(type(value) is int and 0 <= value <= maximum, f"packet number must be 0..0x{maximum:X}")
    return value


def packets(declared, require):
    """A packets array as normalised (id, subcommand, length, override) tuples, in order."""
    require(isinstance(declared, list) and 0 < len(declared) <= 64, "a mod declares 1-64 packets")
    result = []
    for p in declared:
        require(isinstance(p, dict) and not (set(p) - PACKET_FIELDS), "a mod packet has an unknown field")
        ident = _number(p.get("id"), 0xFF, require)
        sub = _number(p["subcommand"], 0xFFFF, require) if "subcommand" in p else None
        length = p.get("length", -1)
        require(type(length) is int and (length == -1 or 1 <= length <= 32767), "a mod packet length is -1 or 1..32767")
        override = p.get("override", False)
        require(type(override) is bool, "override is true or false")
        require(not (ident == 0xBF and sub in RESERVED_SUBCOMMANDS), f"0xBF subcommand 0x{sub or 0:04X} is reserved by GUO")
        key = (ident, sub, length, override)
        require(all(k[:2] != key[:2] for k in result), "a mod packet is declared twice")
        result.append(key)
    return result


def validate_component(c, m, require):
    """Manifest-only checks, as the client runs them before downloading."""
    if c["type"] != "mod":
        require(all(c.get(k) is None for k in MOD_FIELDS), "mod field on a component that is not a mod")
        return
    files = m["files"]
    require(c["target"] == "client", "a mod component targets the client only")
    require(PurePosixPath(c["entry"]).suffix.lower() == ".json", "a mod component's entry is its mod.json")
    require(all(c.get(k) is None for k in ("references", "language", "runtime", "runtime_version", "capabilities")), "script field on a mod component")
    assembly = c.get("assembly")
    require(isinstance(assembly, str) and assembly in files and PurePosixPath(assembly).suffix.lower() == ".dll", "a mod component names its declared .dll")
    require(folder(assembly) == folder(c["entry"]), "a mod's assembly sits beside its mod.json")
    require(c.get("abi") == ABI, f"mod ABI must be {ABI}")
    packets(c.get("packets"), require)
    require(c.get("gumps") == [], "mod ABI 1 has no gump call; gumps must be empty")
    key = ed25519.decode(c.get("key") or "", 32)
    signature = ed25519.decode(c.get("signature") or "", 64)
    require(ed25519.verify(key, signed_message(m, c), signature), "the mod's publisher signature does not verify")


def validate_assemblies(m, require):
    mods = [c for c in m["components"] if c["type"] == "mod"]
    assemblies = [c["assembly"] for c in mods]
    require(len({a.lower() for a in assemblies}) == len(assemblies), "two mod components share an assembly")
    require(all(PurePosixPath(p).suffix.lower() != ".dll" or p in assemblies for p in m["files"]), "a .dll is allowed only as a mod component's assembly")
    folders = [folder(c["entry"]).lower() for c in mods]
    require(len(set(folders)) == len(folders), "each mod component needs its own folder")


def signed_message(m, c):
    return (f"{SIGNATURE_SCHEMA}\n{m['id']}\n{m['version']}\n{c['id']}\n"
            f"{m['files'][c['entry']]}\n{m['files'][c['assembly']]}\n").encode("utf-8")


def sign(m, component_id, secret):
    """Fills key and signature on one mod component. files must already hold the final hashes."""
    c = next(c for c in m["components"] if c["id"] == component_id and c["type"] == "mod")
    c["key"] = ed25519.encode(ed25519.public_key(secret))
    c["signature"] = ed25519.encode(ed25519.sign(secret, signed_message(m, c)))
    return c


def check_payload(m, read, require):
    """mod.json agrees with its component: abi, assembly and the same packets in the same order."""
    for c in (c for c in m.get("components") or [] if c["type"] == "mod"):
        mod = json.loads(read(c["entry"]).decode("utf-8"))
        require(isinstance(mod, dict) and mod.get("schema") == "guo/mod@1", "a mod's mod.json has the wrong schema")
        require(mod.get("abi") == c["abi"], "the mod's mod.json and its component disagree on abi")
        require(folder(c["entry"]) + str(mod.get("assembly")) == c["assembly"], "the mod's mod.json and its component name different assemblies")
        require(packets(mod.get("packets"), require) == packets(c["packets"], require), "the mod's mod.json and its component declare different packets")
