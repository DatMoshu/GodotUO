from pathlib import Path
import sys

import pytest
from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parent))

import diff  # noqa: E402


def picture(pixels=(), colour=(200, 40, 40)):
    img = Image.new("RGB", (16, 10), (40, 40, 40))
    for x, y in pixels:
        img.putpixel((x, y), colour)
    return img


def test_identical_pictures_differ_nowhere():
    assert diff.count(diff.differing(picture(), picture())) == (0, 0, [])
    assert diff.describe(0, 0) == "0 px"


@pytest.mark.parametrize("colour", [(41, 40, 40), (40, 41, 40), (40, 40, 41), (0, 0, 1), (255, 255, 254)])
def test_a_one_step_change_in_one_channel_counts(colour):
    base = picture(colour=(0, 0, 0)) if colour in ((0, 0, 1),) else picture()
    if colour == (255, 255, 254):
        base = picture(colour=(255, 255, 255))
        base.putpixel((4, 4), (255, 255, 255))
    changed = base.copy()
    changed.putpixel((4, 4), colour)
    assert diff.count(diff.differing(base, changed))[:2] == (1, 1)


def test_black_against_one_blue_step_is_a_difference():
    black = Image.new("RGB", (4, 4), (0, 0, 0))
    blue = Image.new("RGB", (4, 4), (0, 0, 1))
    assert diff.differing(black, blue).getpixel((0, 0)) == 255
    assert diff.count(diff.differing(black, blue))[0] == 16


def test_a_lone_pixel_is_isolated_and_a_patch_is_not():
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
