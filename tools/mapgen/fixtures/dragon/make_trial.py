"""Create a fresh 32 by 24 Dragon 1.05 test workspace. Supply your own Dragon.exe."""
import argparse
import shutil
import struct
from pathlib import Path


def create(exe, dest, reverse=False, recolor=False, random_tiles=False, superset_only=False, edge_lift=False, classic=False):
    dest.mkdir(parents=True, exist_ok=False)
    shutil.copy2(exe, dest / "Dragon.exe")
    (dest / "Statics").mkdir()
    scripts = dest / "Scripts"
    for name in ("Map", "Map2", "Items", "Statics", "Statics2", "Statics3"):
        (scripts / name).mkdir(parents=True)
    for name in ("Betweentrans2", "Statbetweentrans", "Statbetweentrans2",
                 "Statbetweentrans3", "Unevenly", "structs"):
        (scripts / (name + ".txt")).write_text("", encoding="ascii")
    width, height = (6144, 4096) if classic else (32, 24)
    (scripts / "mapcfg.ini").write_text(f"[MAP]\nWIDTH={width}\nHEIGHT={height}\n", encoding="ascii")
    variants = "0003 0004" if random_tiles else "0003"
    if classic:
        (scripts / "groups.txt").write_text("00 00\n01 01\n02 00\n", encoding="ascii")
        table = f"00 007 {variants}\n01 -05 0016\n02 019 {variants}\n"
    else:
        table = f"00 00 007 {variants}\n01 01 -05 0016\n02 00 019 {variants}\n"
    (scripts / "Maptrans.txt").write_text(table, encoding="ascii")
    (scripts / "Betweentrans.txt").write_text("00 01 trial.txt\n", encoding="ascii")
    rows = ["AABBBAAA 0038 000 000", "AABBBAAA 0039 000 000"]
    if reverse:
        rows.reverse()
    if superset_only:
        rows = ["BBBBBBBB 0039 000 000"]
    if edge_lift:
        rows = ["AABBBAAA 0039 003 003"]
    (scripts / "Map/trial.txt").write_text("\n".join(rows) + "\n", encoding="ascii")
    (scripts / "Items.txt").write_text("00 1000 0006 0006 0010 0010 marker.txt\n", encoding="ascii")
    (scripts / "Items/marker.txt").write_text("0001 0EED 0002 0021 0000 0000\n", encoding="ascii")
    (dest / "dragon.ini").write_text(
        "[Options]\nBMPFile=trial.bmp\nMAPFile=map0.mul\nFixFile=\nRemoveStatics=1\n"
        f"HighPrior=0\nMapWidth={width // 8}\nMapHeight={height // 8}\n"
        "[UOSPDRAGON]\nStaidx0=staidx0.mul\nStatics0=statics0.mul\n", encoding="ascii")
    palette = b"".join(bytes((255-i, 42, 73, 0) if recolor else (i, i, i, 0)) for i in range(256))
    row = bytes(17) + bytes([1]) * (width - 17)
    marked = row[:7] + bytes([2]) + row[8:]
    raster = row * (height - 10) + marked + row * 9  # bottom-up rows
    offset = 14 + 40 + len(palette)
    bmp = struct.pack("<2sIHHI", b"BM", offset + len(raster), 0, 0, offset)
    bmp += struct.pack("<IiiHHIIiiII", 40, width, height, 1, 8, 0, len(raster), 0, 0, 256, 0)
    (dest / "trial.bmp").write_bytes(bmp + palette + raster)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("exe", type=Path)
    parser.add_argument("destination", type=Path)
    rules = parser.add_mutually_exclusive_group()
    rules.add_argument("--reverse", action="store_true")
    parser.add_argument("--recolor", action="store_true")
    parser.add_argument("--random-tiles", action="store_true")
    rules.add_argument("--superset-only", action="store_true")
    rules.add_argument("--edge-lift", action="store_true")
    parser.add_argument("--dragonsp", type=Path, help="also stage your DragonSP and empty static files")
    parser.add_argument("--classic", action="store_true", help="1.03.62 tables and a full 6144 by 4096 BMP")
    args = parser.parse_args()
    create(args.exe, args.destination, args.reverse, args.recolor, args.random_tiles, args.superset_only, args.edge_lift, args.classic)
    if args.dragonsp:
        shutil.copy2(args.dragonsp, args.destination / "Dragonsp.exe")
        (args.destination / "statics0.mul").write_bytes(b"")
        (args.destination / "staidx0.mul").write_bytes(struct.pack("<iii", -1, -1, -1) * 12)
