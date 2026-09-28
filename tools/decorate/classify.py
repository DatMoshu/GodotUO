"""What a furnishing is, and what a room full of them is for, from tiledata names only.

Simple word rules: an item's kind is the first rule its name matches; a room's
type is the rule whose kinds score highest in it. Everything here is a plain
table so a later pass (dungeons, fields, camps) adds rows rather than code.
"""
from __future__ import annotations

import collections
import re

# TileFlag bits (as tools/multi/pieces.py)
BACKGROUND, WEAPON, TRANSPARENT, TRANSLUCENT, WALL = 0x1, 0x2, 0x4, 0x8, 0x10
DAMAGING, IMPASSABLE, WET, SURFACE, BRIDGE = 0x20, 0x40, 0x80, 0x200, 0x400
GENERIC, WINDOW, NOSHOOT, FOLIAGE = 0x800, 0x1000, 0x2000, 0x20000
LIGHT_SOURCE = 0x800000
ROOF, DOOR = 0x10000000, 0x20000000

# Names that are building fabric or ground, not furnishing, even when tiledata calls them
# "deco": planks, stair blocks, water, grass, roofing, rubble.
STRUCTURE = {"wooden plank", "wood", "stone", "sandstone", "marble", "water", "grasses", "grass", "hedge",
             "stone roof", "platform", "nodraw", "brick", "bricks", "cobblestones", "dirt", "rock", "rocks",
             "rubble", "wooden boards", "boards", "flagstone", "flagstones", "tile", "tiles", "sand",
             "wooden post", "stone post", "roof", "thatch", "wooden wall", "stone wall", "pavers",
             "shingles", "slate", "palisade", "fence", "iron fence", "wooden fence", "shallow water",
             "dungeon", "cave", "mud", "puddle", "ground", "log", "logs"}

FABRIC = re.compile(r"banister|railing|rostrum|stalagmite|\bstep\b|\bsteps\b|\bstairs?\b|\bcolumn\b|\bpillar\b"
                    r"|\barch\b|\bbeam\b|\bgirder\b|\bsupport\b|bridge|brambles|garbage|debris|flowstone|\brope\b|bloody water")

# kind -> name patterns (whole words, first match wins; order matters)
KIND_RULES: list[tuple[str, str]] = [
    ("remains", r"\bskulls?\b|\bbones?\b|\bskeleton|\bcorpse\b|\bblood|entrails|\bbroken\b|worldgem|\bruined\b|\brotting\b|\bgore\b"),
    ("bed", r"\bbed\b|bedroll|\bcot\b"),
    ("forge", r"\bforge\b|\bbellows\b"),
    ("anvil", r"\banvil\b"),
    ("oven", r"\boven\b|\bstove\b|fire pit"),
    ("fireplace", r"fireplace|hearth"),
    ("loom", r"\bloom\b|spinning wheel|dress form"),
    ("bookcase", r"bookcase|bookshelf|book shelf"),
    ("book", r"pen and ink|\bbooks?\b|\bscrolls?\b|\bspellbook"),
    ("counter", r"\bcounter\b"),
    ("display", r"display case|\bshelf\b|\bshelves\b|\brack\b"),
    ("table", r"\btable\b|\bdesk\b|\bwriting table\b"),
    ("chair", r"\bchair\b|\bstool\b|\bbench\b|\bthrone\b|\bpew\b"),
    ("chest", r"\bchest\b|\bchest of drawers\b|\barmoire\b|\bdrawers?\b|\bdresser\b|\bwardrobe\b"),
    ("container", r"\bcrate\b|\bbox\b|\bbarrel\b|\bkeg\b|\bcask\b|\bsack\b|\bbasket\b|\bbucket\b|\btub\b"),
    ("food", r"\bbread\b|\bloaf\b|\bcheese\b|\bham\b|\bmeat\b|\bribs?\b|\bfish\b|\bpie\b|\bcake\b|\bdough\b"
             r"|\bflour\b|\bfruit\b|\bapples?\b|\bgrapes\b|\bpears?\b|\bpeach\b|\bmelon\b|\bsquash\b|\bcarrots?\b"
             r"|\bonions?\b|\bpumpkin\b|\bturnip\b|\blettuce\b|\bcabbage\b|\bsausage\b|\bchicken\b|\bbacon\b"),
    ("cookware", r"\bpot\b|\bpots\b|\bpan\b|\bkettle\b|\bcauldron\b|\bskillet\b|rolling pin|\bladle\b"
                 r"|\bspoon\b|\bforks?\b|\btray\b|silverware|\bspittoon\b|\bplatter\b|\bplate\b|\bbowl\b|\bcleaver\b|\bknife\b"),
    ("drink", r"\bbottle\b|\bbottles\b|\bmug\b|\bgoblet\b|\bpitcher\b|\bglass\b|\bjug\b|\bwine\b|\bale\b"
              r"|\bflask\b|\btankard\b|\bcup\b|\bdecanter\b"),
    ("alchemy", r"bandage|\breagent|\bmortar\b|\bpestle\b|\bretort\b|\bvials?\b|\bpotion|\bflasks\b|alembic"),
    ("tools", r"\bhammer\b|\btongs\b|\bsaw\b|\bplane\b|\bdrawknife\b|\bscorp\b|\bfroe\b|\binshave\b"
              r"|\bjointing\b|\bdovetail\b|\bchisel\b|\bpickaxe\b|\bshovel\b|\bsmith\b|\bingots?\b|\bsledge\b"),
    ("woodwork", r"wood curls|\bboard\b|\blumber\b|\bshavings\b|\bsawhorse\b|\bunfinished\b"),
    ("cloth", r"folded sheet|woven mat|\bcloth\b|\bbolt\b|\bthread\b|\byarns?\b|\bwool\b|\bcotton\b|\bflax\b|\bspool\b|\bscissors\b"
              r"|\bhides?\b|\bleather\b|\bshirt\b|\bcloak\b|\bdress\b"),
    ("weapons", r"\bsword\b|\baxe\b|\bmace\b|\bspear\b|\bhalberd\b|\bbow\b|\bcrossbow\b|\bshield\b"
                r"|\barmou?r\b|\bhelm\b|\bweapon|\btraining dummy\b|\bpickaxe\b|\bdummy\b|\barchery butte\b"),
    ("flora", r"^(white |red |blue |yellow |orange |purple |pink )?flowers$|snowdrops|foxglove|orfluer|poppies"
              r"|\blilies\b|\bgrass|\bweeds?\b|\bmushroom|\bbushe?s?\b|\bshrub|lamp ?post|\bcattails?\b|\breeds?\b|\bvines?\b"),
    ("light", r"\bcandle|\bcandelabra\b|\blamp\b|\blantern\b|\btorch\b|\bsconce\b|\bbrazier\b|\blamppost\b"),
    ("art", r"\bclock\b|\bpainting\b|\bportrait\b|\btapestry\b|\bbanner\b|\bstatue\b|\bstatuette\b|\bbust\b|\bvase\b"
            r"|\bmirror\b|\bpicture\b|\bflag\b|\bdeer head\b|\btrophy\b|\bmounted\b"),
    ("plant", r"flowerpot|\bplant\b|\bflowers?\b|\bpotted\b|\bfern\b|\bcactus\b|\bbonsai\b|\bfoxglove\b|\bsnowdrops?\b"
              r"|\bpoppies\b|\borfluer\b|\brose\b|\bivy\b"),
    ("religious", r"\baltar\b|\bankh\b|\bshrine\b|\bcross\b|\breliquary\b"),
    ("music", r"\bharp\b|\blute\b|\bdrum\b|\btambourine\b|\blap harp\b|\bpiano\b|\bmusic\b"),
    ("farm", r"\bhay\b|\bstraw\b|\btrough\b|\bwheat\b|\bsheaf\b|\bsaddle\b|\bplow\b|\bpitchfork\b|\bchurn\b"),
    ("bank", r"\bgold\b|\bcoins?\b|\bscales?\b|\bstrongbox\b|\bingot\b"),
    ("map", r"\bmaps?\b|\bglobe\b|\bcharts?\b|\bsextant\b|\bspyglass\b|\bcompass\b"),
    ("rug", r"\brug\b|\bcarpet\b|\bfur\b|\bpelt\b|\bbearskin\b"),
    ("bath", r"\bwash|\bbasin\b|\bbath\b|\bwater tub\b|\bpump\b"),
]
_KIND_RE = [(k, re.compile(p)) for k, p in KIND_RULES]

# room type -> (kind weights, minimum score). The type with the highest score over its
# minimum wins; ties go to the earlier row.
ROOM_RULES: list[tuple[str, dict[str, float], float]] = [
    ("smithy", {"forge": 6, "anvil": 6, "tools": 1.5, "weapons": 1}, 6),
    ("bakery-kitchen", {"oven": 6, "food": 1.5, "cookware": 1.5, "fireplace": 1, "container": 0.3}, 5),
    ("tailor", {"loom": 6, "cloth": 2}, 6),
    ("carpenter", {"woodwork": 3, "tools": 1}, 6),
    ("alchemist", {"alchemy": 2.5}, 6),
    ("bank", {"bank": 2, "counter": 0.5}, 8),
    ("shrine", {"religious": 4, "light": 0.5}, 4),
    ("library", {"bookcase": 3, "book": 1.2, "table": 0.3, "map": 0.5}, 6),
    ("shop", {"counter": 2, "display": 1.5, "weapons": 0.5, "cloth": 0.3}, 6),
    ("bedroom", {"bed": 5, "chest": 1, "light": 0.3, "rug": 0.5}, 5),
    ("tavern-hall", {"drink": 1.2, "chair": 0.8, "table": 0.6, "container": 0.4, "food": 0.6, "music": 1}, 9),
    ("barracks-armoury", {"weapons": 2}, 6),
    ("stable-farm", {"farm": 2}, 5),
    ("kitchen", {"food": 1.2, "cookware": 1.5, "fireplace": 2}, 5),
    ("storage", {"container": 1.2, "chest": 1}, 4),
    ("dining-hall", {"table": 1, "chair": 1}, 3),
    ("parlour", {"art": 1, "plant": 1, "light": 0.7, "rug": 1, "chair": 0.5, "table": 0.5, "fireplace": 1}, 2),
]
ROOM_TYPES = [r[0] for r in ROOM_RULES] + ["sparse", "empty"]


def kind_of(name: str) -> str:
    n = name.lower()
    for k, rx in _KIND_RE:
        if rx.search(n):
            return k
    return "misc"


def is_furnishing(tile: dict) -> bool:
    """A tile a room is furnished with: not fabric (walls, floors, roofs, doors,
    stairs), not ground (water, grass), not plants growing out of the ground."""
    f, name = tile["flags"], tile["name"].strip().lower()
    if f & (WALL | ROOF | DOOR | WINDOW | FOLIAGE | 0x40000000 | 0x80000000):
        return False
    if name in STRUCTURE or not name or name.startswith("item ") or FABRIC.search(name):
        return False
    if f & SURFACE and tile["height"] == 0:
        return kind_of(name) == "rug"            # floor tiles, except rugs
    if f & WET and "tub" not in name and "trough" not in name:
        return False
    return True


def classify(kinds: collections.Counter) -> tuple[str, float]:
    """A room's type from the counts of its furnishing kinds, and the winning score."""
    if not kinds:
        return "empty", 0.0
    best, best_score = None, 0.0
    for name, weights, minimum in ROOM_RULES:
        s = sum(w * min(kinds.get(k, 0), 8) for k, w in weights.items())
        if s >= minimum and s > best_score:
            best, best_score = name, s
    if best is None:
        return "sparse", float(sum(kinds.values()))
    return best, round(best_score, 2)


def facing_from_wall(against: str) -> str:
    """An item with its back to a wall faces the opposite way: back to N faces S."""
    opp = {"N": "S", "S": "N", "E": "W", "W": "E"}
    return "".join(opp[d] for d in against) if against and len(against) == 1 else ""
