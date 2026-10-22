from pathlib import Path
import sys

from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parent))

import diff  # noqa: E402


def picture(pixels=()):
    img = Image.new("RGB", (16, 10), (40, 40, 40))
    for x, y in pixels:
        img.putpixel((x, y), (200, 40, 40))
    return img


def test_identical_pictures_differ_nowhere():
    assert diff.count(diff.differing(picture(), picture())) == (0, 0, [])
    assert diff.describe(0, 0) == "0 px"


def test_a_lone_texel_is_isolated_and_a_patch_is_not():
    changed = picture([(3, 3), (10, 5), (11, 5), (10, 6)])
    total, isolated, where = diff.count(diff.differing(picture(), changed))
    assert (total, isolated, where) == (4, 1, [(3, 3)])
    assert diff.describe(total, isolated) == "4 px (1 isolated)"


def test_diagonal_neighbours_count_as_a_patch():
    changed = picture([(5, 5), (6, 6)])
    assert diff.count(diff.differing(picture(), changed))[:2] == (2, 0)


def test_edges_and_corners_are_safe():
    changed = picture([(0, 0), (15, 9)])
    total, isolated, where = diff.count(diff.differing(picture(), changed))
    assert (total, isolated, where) == (2, 2, [(0, 0), (15, 9)])
