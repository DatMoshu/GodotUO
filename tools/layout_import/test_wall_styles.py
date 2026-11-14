"""Wall-family regression: geometry roles must not follow noisy mined signatures."""
import copy
import unittest
from layout_import import native

class WallStyles(unittest.TestCase):
    def test_four_corners_and_runs_stay_in_rubble_set(self):
        for sig, item in {"ES":0xcc,"SW":0xc8,"NE":0xce,"NW":0xc7,"EW":0xc8,"NS":0xc9}.items():
            self.assertEqual(native.wall_piece(None, native.DEFAULT_THEME, sig), item)
    def test_windows_stay_in_same_family(self):
        self.assertEqual(native.wall_piece(None,native.DEFAULT_THEME,"EW",True),0xca)
        self.assertEqual(native.wall_piece(None,native.DEFAULT_THEME,"NS",True),0xcb)
    def test_material_override_does_not_use_stone(self):
        class Fake:
            def wall(self,*args): return args
        theme=copy.deepcopy(native.DEFAULT_THEME);theme["materials"]["wall"]="plaster"
        self.assertEqual(native.wall_piece(Fake(),theme,"NW"),("plaster",20,"NW",False))
    def test_wrong_height_refused(self):
        theme=copy.deepcopy(native.DEFAULT_THEME);theme["wall_height"]=40
        with self.assertRaises(native.ResolutionError):native.wall_piece(None,theme,"NW")

if __name__=="__main__":unittest.main()
