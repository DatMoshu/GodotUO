"""Registry of the UO client data files the port reads at runtime.

This is the machine-readable half of docs/data_formats.md. Keep the two in
step: if you add an entry here, document what the runtime does with it there.

Nothing in this module reads or decodes game content. It records which files
must exist, which are optional, and which legacy .mul files a modern client
replaces with a .uop archive. The decoding itself lives in the C# runtime
(godot/GUO/src/IO), ported from ClassicUO.
"""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class DataFile:
    """One logical piece of client data.

    A logical file may be satisfied by more than one physical file, because
    clients from roughly 7.0.24 onward pack several legacy `.mul` files into
    `.uop` archives. `mul` lists the classic form; `uop` the modern one.
    Either satisfying the entry is enough.
    """

    key: str
    """Stable identifier used by the runtime and the manifest."""

    description: str
    """What the port uses this data for."""

    mul: tuple[str, ...] = ()
    """Classic-format filenames (matched case-insensitively)."""

    uop: tuple[str, ...] = ()
    """UOP-archive filenames that replace `mul` on newer clients."""

    required: bool = True
    """False for data the client degrades gracefully without."""

    subsystem: str = ""
    """Which port subsystem consumes it. Mirrors docs/port_plan.md."""

    indexed_by: tuple[str, ...] = ()
    """Companion index files that must accompany the `mul` form."""

    notes: str = ""

    def present_forms(self, data_dir: Path) -> dict[str, list[str]]:
        """Return which physical forms of this entry exist in `data_dir`."""
        found = index_dir(data_dir)
        # Report the names as they are on disk, so a caller can open them on
        # a case-sensitive filesystem.
        return {
            "mul": [found[n.lower()] for n in self.mul if n.lower() in found],
            "uop": [found[n.lower()] for n in self.uop if n.lower() in found],
            "index": [found[n.lower()] for n in self.indexed_by if n.lower() in found],
        }

    def is_satisfied(self, data_dir: Path) -> bool:
        """True when some complete form of this data is available."""
        forms = self.present_forms(data_dir)
        if forms["uop"]:
            return True
        if forms["mul"]:
            # A classic .mul is only usable together with its index.
            return not self.indexed_by or bool(forms["index"])
        return False


def index_dir(data_dir: Path) -> dict[str, str]:
    """Filenames in `data_dir`, lowercased name -> name on disk, for
    case-insensitive lookup.

    UO installs are inconsistent about capitalisation (`Gumpart.mul` vs
    `gumpart.mul`), and the port must run on case-sensitive filesystems too.
    """
    try:
        return {p.name.lower(): p.name for p in data_dir.iterdir() if p.is_file()}
    except (OSError, FileNotFoundError):
        return {}


FACETS = ("Felucca", "Trammel", "Ilshenar", "Malas", "Tokuno", "TerMur")


def _facet_entries() -> list[DataFile]:
    """Per-facet terrain, statics, and their patch layers.

    Three physical variants exist for a given facet and the runtime must pick
    in this order:

      1. `mapNxLegacyMUL.uop` / `staticsNx.mul` -- the "expanded" form shipped
         by later clients after Felucca and Trammel were enlarged. When the x
         variant is present it supersedes the plain one entirely.
      2. `mapNLegacyMUL.uop` -- UOP packaging of the classic map.
      3. `mapN.mul` -- the classic form.

    On top of whichever is chosen, the `mapdif`/`stadif` layers patch
    individual blocks. They are applied last and are optional.
    """
    entries: list[DataFile] = []
    for i, name in enumerate(FACETS):
        entries.append(
            DataFile(
                key=f"map{i}",
                description=f"{name} terrain heightmap and tile ids",
                mul=(f"map{i}.mul",),
                uop=(f"map{i}LegacyMUL.uop", f"map{i}xLegacyMUL.uop"),
                required=(i == 0),  # only Felucca is needed to boot
                subsystem="world/terrain",
                notes=(
                    f"Facet {i}. The x variant, when present, is the expanded "
                    "map and wins over the plain one."
                ),
            )
        )
        entries.append(
            DataFile(
                key=f"statics{i}",
                description=f"{name} static item placements",
                mul=(f"statics{i}.mul", f"statics{i}x.mul"),
                indexed_by=(f"staidx{i}.mul", f"staidx{i}x.mul"),
                required=(i == 0),
                subsystem="world/statics",
                notes="The x variant pairs with the expanded map of the same facet.",
            )
        )
        entries.append(
            DataFile(
                key=f"mapdif{i}",
                description=f"{name} terrain patch blocks",
                mul=(f"mapdif{i}.mul",),
                indexed_by=(f"mapdifl{i}.mul",),
                required=False,
                subsystem="io/patching",
                notes="Applied over the base terrain after it loads.",
            )
        )
        entries.append(
            DataFile(
                key=f"stadif{i}",
                description=f"{name} statics patch blocks",
                mul=(f"stadif{i}.mul",),
                indexed_by=(f"stadifi{i}.mul", f"stadifl{i}.mul"),
                required=False,
                subsystem="io/patching",
                notes="Applied over the base statics after they load.",
            )
        )
        entries.append(
            DataFile(
                key=f"facet{i:02d}",
                description=f"{name} facet metadata block",
                mul=(f"facet{i:02d}.mul",),
                required=False,
                subsystem="world/terrain",
            )
        )
    return entries


FILE_REGISTRY: tuple[DataFile, ...] = (
    # --- core tables: nothing renders without these ---
    DataFile(
        key="tiledata",
        description="Per-tile flags, names, heights and weights for land and statics",
        mul=("tiledata.mul",),
        subsystem="world/tiledata",
        notes="Read first at boot; drives collision, render order and naming.",
    ),
    DataFile(
        key="hues",
        description="Colour palettes applied to art, text and equipment",
        mul=("hues.mul",),
        subsystem="render/hues",
        notes="Ported to a Godot texture LUT sampled in shader.",
    ),
    DataFile(
        key="radarcol",
        description="Per-tile minimap colours",
        mul=("radarcol.mul",),
        required=False,
        subsystem="ui/minimap",
    ),
    # --- art ---
    DataFile(
        key="art",
        description="Land and static item sprites",
        mul=("art.mul",),
        indexed_by=("artidx.mul",),
        uop=("artLegacyMUL.uop",),
        subsystem="render/art",
    ),
    DataFile(
        key="texmaps",
        description="Terrain texture maps used for sloped land",
        mul=("texmaps.mul",),
        indexed_by=("texidx.mul",),
        subsystem="render/terrain",
    ),
    DataFile(
        key="gumpart",
        description="UI gump images",
        mul=("gumpart.mul",),
        indexed_by=("gumpidx.mul",),
        uop=("gumpartLegacyMUL.uop",),
        subsystem="ui/gumps",
    ),
    DataFile(
        key="light",
        description="Light source falloff sprites",
        mul=("light.mul",),
        indexed_by=("lightidx.mul",),
        required=False,
        subsystem="render/lighting",
    ),
    DataFile(
        key="multi",
        description="Multi-tile structures (houses, boats)",
        mul=("multi.mul",),
        indexed_by=("multi.idx",),
        uop=("MultiCollection.uop",),
        subsystem="world/multis",
    ),
    # --- animation ---
    DataFile(
        key="anim",
        description="Creature and player body animations (primary archive)",
        mul=("anim.mul",),
        indexed_by=("anim.idx",),
        uop=("AnimationFrame1.uop",),
        subsystem="render/animation",
    ),
    DataFile(
        key="anim_extra",
        description="Expansion animation archives",
        mul=("anim2.mul", "anim3.mul", "anim4.mul", "anim5.mul", "anim6.mul"),
        indexed_by=(
            "anim2.idx",
            "anim3.idx",
            "anim4.idx",
            "anim5.idx",
            "anim6.idx",
        ),
        uop=(
            "AnimationFrame2.uop",
            "AnimationFrame3.uop",
            "AnimationFrame4.uop",
            "AnimationFrame6.uop",
        ),
        required=False,
        subsystem="render/animation",
        notes="Which archives exist depends on the expansions installed.",
    ),
    DataFile(
        key="animation_sequence",
        description="Per-body animation sequence and replacement overrides",
        uop=("AnimationSequence.uop",),
        required=False,
        subsystem="render/animation",
        notes=(
            "Modern clients only. Remaps and retimes animation groups; without "
            "it newer bodies play the wrong action."
        ),
    ),
    DataFile(
        key="animinfo",
        description="Animation frame timing table",
        mul=("animinfo.mul",),
        required=False,
        subsystem="render/animation",
    ),
    DataFile(
        key="animdata",
        description="Animated-tile cycling data (water, flames, forge)",
        mul=("animdata.mul",),
        required=False,
        subsystem="render/art",
    ),
    # --- text ---
    DataFile(
        key="fonts",
        description="Classic bitmap fonts",
        mul=("fonts.mul",),
        subsystem="ui/text",
    ),
    DataFile(
        key="unifont",
        description="Unicode fonts",
        mul=("unifont.mul",) + tuple(f"unifont{i}.mul" for i in range(1, 13)),
        required=False,
        subsystem="ui/text",
        notes="unifont.mul is the practical minimum; the rest are per-locale.",
    ),
    DataFile(
        key="cliloc",
        description="Localised server message strings",
        mul=("cliloc.enu",),
        subsystem="ui/text",
        notes="Locale suffix varies (.enu/.deu/.fra). Server sends ids, not text.",
    ),
    DataFile(
        key="speech",
        description="Keyword table for NPC speech triggers",
        mul=("speech.mul",),
        required=False,
        subsystem="game/speech",
    ),
    # --- audio ---
    DataFile(
        key="sound",
        description="Sound effects",
        mul=("sound.mul",),
        indexed_by=("soundidx.mul",),
        uop=("soundLegacyMUL.uop",),
        required=False,
        subsystem="audio/sfx",
    ),
    # --- character ---
    DataFile(
        key="skills",
        description="Skill names and usability flags",
        mul=("skills.mul",),
        indexed_by=("skills.idx",),
        subsystem="game/skills",
    ),
    DataFile(
        key="professions",
        description="Character-creation profession presets",
        mul=("prof.txt",),
        required=False,
        subsystem="game/charcreate",
    ),
    # --- definition overrides (plain text, shard-editable) ---
    DataFile(
        key="body_def",
        description="Body id remapping",
        mul=("body.def",),
        required=False,
        subsystem="render/animation",
    ),
    DataFile(
        key="bodyconv_def",
        description="Body id to animation archive routing",
        mul=("bodyconv.def",),
        required=False,
        subsystem="render/animation",
    ),
    DataFile(
        key="gump_def",
        description="Gump id remapping",
        mul=("gump.def",),
        required=False,
        subsystem="ui/gumps",
    ),
    DataFile(
        key="verdata",
        description="Legacy patch archive that overrides entries in other files",
        mul=("verdata.mul",),
        required=False,
        subsystem="io/patching",
        notes="Rare on modern clients but must be honoured when present.",
    ),
    # --- modern-client archives (roughly 7.0.24+) -------------------------
    DataFile(
        key="tileart",
        description="High-definition replacement art keyed by tile id",
        uop=("tileart.uop",),
        required=False,
        subsystem="render/art",
        notes="Overrides art.mul entries when present. Ignore for classic look.",
    ),
    DataFile(
        key="string_dictionary",
        description="Packed string table used instead of cliloc on new clients",
        uop=("string_dictionary.uop",),
        required=False,
        subsystem="ui/text",
        notes="Where both exist, cliloc.enu remains authoritative for the port.",
    ),
    DataFile(
        key="mainmisc",
        description="Assorted client UI resources",
        uop=("MainMisc.uop",),
        required=False,
        subsystem="ui/gumps",
    ),
    # --- lookup tables ----------------------------------------------------
    DataFile(
        key="palette",
        description="Base 256-colour palette",
        mul=("palette.mul",),
        required=False,
        subsystem="render/hues",
    ),
    DataFile(
        key="skillgrp",
        description="Skill grouping for the skills gump",
        mul=("skillgrp.mul",),
        required=False,
        subsystem="ui/gumps",
    ),
    DataFile(
        key="sjis2uni",
        description="Shift-JIS to Unicode mapping for the Japanese client",
        mul=("sjis2uni.mul",),
        required=False,
        subsystem="ui/text",
    ),
    # --- .def overrides: plain text, commonly edited by shards ------------
    DataFile(
        key="art_def",
        description="Art id remapping",
        mul=("art.def",),
        required=False,
        subsystem="render/art",
    ),
    DataFile(
        key="anim_def",
        description="Animation archive id remapping",
        mul=("anim1.def", "anim2.def", "anim3.def", "anim4.def", "anim5.def"),
        required=False,
        subsystem="render/animation",
    ),
    DataFile(
        key="corpse_def",
        description="Corpse body id remapping",
        mul=("corpse.def",),
        required=False,
        subsystem="render/animation",
    ),
    DataFile(
        key="equipconv_def",
        description="Equipment-to-body animation conversion",
        mul=("equipconv.def",),
        required=False,
        subsystem="render/animation",
        notes="Needed for equipped items to animate on non-human bodies.",
    ),
    DataFile(
        key="music_def",
        description="Music track id to filename mapping",
        mul=("music.def", "music/digital/config.txt"),
        required=False,
        subsystem="audio/music",
        notes="Tracks are loose files under Music/; this maps server ids to them.",
    ),
    DataFile(
        key="sound_def",
        description="Sound id remapping",
        mul=("sound.def",),
        required=False,
        subsystem="audio/sfx",
    ),
    DataFile(
        key="intrface_def",
        description="Interface element layout overrides",
        mul=("intrface.def",),
        required=False,
        subsystem="ui/gumps",
    ),
    DataFile(
        key="stitchin_def",
        description="Terrain stitching rules between texture types",
        mul=("stitchin.def",),
        required=False,
        subsystem="render/terrain",
    ),
    DataFile(
        key="texterr_def",
        description="Terrain texture error fallbacks",
        mul=("texterr.def",),
        required=False,
        subsystem="render/terrain",
    ),
    # --- client-side localisation (.enu / locale suffix) ------------------
    DataFile(
        key="intloc",
        description="Client interface strings",
        mul=tuple(f"intloc{i:02d}.enu" for i in range(0, 13)),
        required=False,
        subsystem="ui/text",
        notes="Static client chrome, distinct from server-sent cliloc ids.",
    ),
    DataFile(
        key="gump_text",
        description="Per-gump static text tables",
        mul=(
            "gt_0000.enu",
            "gt_1010.enu",
            "gt_2000.enu",
            "gt_2310.enu",
            "gt_2400.enu",
            "gt_4000.enu",
            "gt_5000.enu",
            "gt_5400.enu",
        ),
        required=False,
        subsystem="ui/text",
    ),
    DataFile(
        key="ui_text",
        description="Assorted UI string tables",
        mul=(
            "tooltips.enu",
            "options.enu",
            "optnuotd.enu",
            "professn.enu",
            "skilname.enu",
            "skill16.enu",
            "skill30.enu",
            "chat.enu",
            "gesture.enu",
            "intro.enu",
            "tilehelp.enu",
        ),
        required=False,
        subsystem="ui/text",
    ),
    *_facet_entries(),
)


def required_files() -> tuple[DataFile, ...]:
    """Entries the client cannot boot without."""
    return tuple(f for f in FILE_REGISTRY if f.required)


def uop_equivalent(mul_name: str) -> str | None:
    """Given a classic .mul filename, the .uop archive that replaces it."""
    target = mul_name.lower()
    for entry in FILE_REGISTRY:
        if target in {m.lower() for m in entry.mul} and entry.uop:
            return entry.uop[0]
    return None


def by_subsystem() -> dict[str, list[DataFile]]:
    """Group the registry by the port subsystem that consumes each entry."""
    grouped: dict[str, list[DataFile]] = {}
    for entry in FILE_REGISTRY:
        grouped.setdefault(entry.subsystem or "unassigned", []).append(entry)
    return grouped

# User-content contracts, separate from the proprietary client data registry.
STORE_PACK_SCHEMA = "guo/store-pack@1"
STORE_INDEX_SCHEMA = "guo/store-index@1"
