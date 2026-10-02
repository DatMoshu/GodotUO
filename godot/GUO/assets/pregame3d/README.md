# pregame3d art

Models for the 3D pregame (see `docs/ui/pregame_3d.md`). Units are metres, Y up, the front faces +Z.
Textures are embedded in each glb. Each material is a Principled material with a base-colour texture
(nearest filter; tiling textures repeat, the rest clamp). Positions come from `layout.json`.

| File | Contents | Tris | Textures | Source |
|---|---|---|---|---|
| `chest.glb` | `ChestBody` (origin bottom-centre, 1.0 x 0.55 x 0.6), `ChestLid` (origin on the hinge axis at the back-top edge, modelled closed; open it by rotating about local X, negative values open it), `ChestInterior` (tufted velvet, padded false bottom at 0.30) | 316 + 228 + 10 | 256² wood, lid planks (one texture width across the lid, no seam), velvet; 64² iron | original, authored in Blender for GUO |
| `cloth.glb` | `Cloth`: red velvet, cloth-simulated over a hidden 1.12 x 0.66 x 0.30 plinth and pooled on the floor, rising behind the chest. Fold shading is in `COLOR_0` (multiply it into albedo). 1155 verts | 2176 | 256² velvet | original, authored in Blender for GUO |
| `wall.glb` | `Wall` (rough stone, 7.2 x 4.4, z = -0.78), `Floor` (flagstones) | 396 + 120 | 256² stone, 64² floor | original, authored in Blender for GUO |
| `shield.glb` | `Shield`: green heater shield with a gold rim and a flat face for the "Quit" text; origin centre-back | 124 | 64² enamel, gold, brass | original, authored in Blender for GUO |
| `plaque_login.glb` | `LoginPlaque` (0.86 x 0.60 oval granite plaque with a carved rim, origin centre-back), `FieldAccount` / `FieldPassword` (0.56 x 0.11 pale inlaid strips with dark bezels at plaque-local y = +0.085 / -0.085; origin at the strip's front-face centre). In the layout each field is about 158 x 35 px at 640x400 | 156 + 24 + 24 | 256² granite, 128² field, 64² iron | original, authored in Blender for GUO |
| `button_login.glb` | `LoginButton`: 0.30 x 0.095 red plaque in a gold trim; origin back-centre, presses in along local -Z | 60 | 64² | original, authored in Blender for GUO |
| `plaque_credits.glb` | `CreditsPlaque`: blue enamel plaque in a brass frame with rivets; origin back-centre | 100 | 64² | original, authored in Blender for GUO |
| `stud.glb` | `StudOff` (brass-framed empty iron socket), `StudOn` (gold boss + latch bar); origin back-centre | 88 / 100 | 64² | original, authored in Blender for GUO |
| `scroll.glb` | `Scroll`: rolled parchment with a ribbon tie and brass knobs, axis along X, origin centre | 108 | 64² parchment, 32² ribbon/brass | original, authored in Blender for GUO |
| `card.glb` | `Card`: parchment card with a wax seal and ribbon; origin centre-back | 52 | 64², 32² | original, authored in Blender for GUO |
| `plinth.glb` | `Plinth`: octagonal stone stand with a wooden top (top at 0.12); origin bottom-centre | 84 | 256² granite, wood | original, authored in Blender for GUO |
| `candle.glb` | `Candle` (brass holder + candle, origin bottom-centre), `Flame` (crossed alpha quads, emissive, origin at its base, which sits on the wick at 0.18) | 120 + 4 | 64², 32², 32x64 flame | original, authored in Blender for GUO |
| `torch.glb` | `Torch` (wall plate at x = 0; stick straightened so the flame points up; leans slightly along +X: use rotation_deg y = -90 on a wall facing +Z), `Flame` (split out from the source mesh) | 22 + 4 | 64², 32x64 | PSX_Library (Moshu), `13_Medieval_Fantasy/lighting/wall_torch.glb` |
| `layout.json` | Composed login scene: camera (16:10, 640x400 internal resolution), world transforms per node, lid angle, lights, ambient colour, `scroll_slots` / `plinth_slots` / `card_slots`, and `step_cameras` for login, servers and characters | | | original |

Notes for `layout.json`: object keys are `<file>#<Node>` or `<file>#<Node>@<instance>`. Each value is the
node's world transform and replaces its in-file transform. Rotations are Godot `rotation_degrees` (YXZ). The plaque, fields, Login button, studs and cards are rotated to face the layout camera. The Quit shield and the Credits plaque lie parallel to the camera's image plane, with no roll, so they read as flat UI. Their `ui_anchors` entries (`corner`, `margin_px` at 1280x800, `height_frac`, `depth_m` along the camera's forward axis) let the game pin them to the screen corners at any aspect ratio. The fields, the button and the scroll slots are coplanar with the plaque, and the `servers` camera looks straight down the plaque's normal.
Plinth and card slots and the candle sit on the simulated cloth surface (raycast). Scroll slots are in the
plaque's plane, so the plaque is meant to be hidden on the server step.

Licence: the files marked "original" are original art for GUO, BSD-2-Clause with the repository.
`torch.glb` is from the team's PSX_Library (Moshu), used with permission.
