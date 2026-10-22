# What HD-2D lighting does that Realtime lights does not

A review of an "HD-2D" ClassicUO fork's lighting, read from a build shared with
the project, against Realtime lights (ADR-0039, data_formats section 45). The
review read the C# and the compiled shaders' instructions and constants.
Nothing from that build (code, shaders, art) is in GUO; this page describes
techniques in our own words. Shader details are inferred from bytecode, not
source.

## How HD-2D lights a frame

1. **A world-position g-buffer.** A float target holds, per pixel, the world
   point (tile X, tile Y, Z) and a surface type: floor, south wall face, east
   wall face, upright (mobiles, trees, lamps), roof. Land is drawn as diamonds
   with corner heights. Sprites are drawn again with alpha test, and each
   pixel's point comes from its plane:
   - a floor at its top Z;
   - a wall on its tile's south or east edge;
   - an upright as a billboard through its foot.
   The surface type stands in for a normal; nothing is derived from the art's
   shading.
2. **Polar shadow maps.** Up to 10 lights, nearest the player first, each get
   a 1024 x 128 strip (angle around the light x height) of nearest-blocker
   distance. Casters are statics, foliage and items cut into cards, walls at
   their true heights, and mobiles as a silhouette taken from the animation
   frame that faces the light. A light casts from 30 Z above its tile (24 when
   carried), and caster height has a soft knee so shadows stay finite.
3. **Per-light lit quads.** Each light is one quad over the g-buffer, masked by
   stencil and added into a light target cleared to the ambient. The light at
   each pixel is:
   - a `(1 - d²/r²)²` falloff, with the light about 8 to 10 Z up;
   - N·L from the surface type, uprights lit in front and dim behind;
   - a per-light tile visibility mask (5 rays per tile through edge spans,
     cached for 4 s);
   - a PCSS-style soft shadow lookup, with 15 % of the light kept in shadow;
   - a three-sine flicker.
4. **Soften.** A two-pass blur of the light target that rejects taps of
   another surface type or height (bilateral), then a multiply over the world.
5. **Ambient.** The server level, remapped (levels from 20 up become 28), with
   a 3 % floor and a cool glow around the player. There is no time-of-day
   colour and no sky shadow.

Cost, by its own notes: one baseline line, 60 fps and 16.7 ms a frame in a busy
street, with 2.0 ms for the world draw. The light passes are not measured
separately. The design has hard caps: 10 shadowed lights, 160 casters and 1500
wall quads per light, 127 masked lights.

## Where Realtime lights stands

| | HD-2D | Realtime lights |
|---|---|---|
| Where light is evaluated | per pixel, at a g-buffer world point | per pixel, at the sprite corner's world point, from a field of 4 samples per tile side |
| Walls | south/east planes from the tile edge; side from the art | lines on their tile from the art's silhouette; each face reads the field in front of it |
| Light vs surface angle | N·L per surface type | none: a face takes the light in front of it at full strength |
| Occlusion | per-light tile visibility plus polar shadow maps | cached per-light reach maps through wall lines, blocker tops and a clearance |
| Shadows of mobiles, trees, lamps | yes, from each light | from the sky only (LIGHTS-05), not from lamps |
| Sky | none | time-of-day tint, sun and moon shadows (LIGHTS-04, -05) |
| Light shapes | point, flicker | point, spot with sweep, flash, presets with flicker (LIGHTS-06) |
| Passes and targets | float g-buffer, shadow-map target, stencil, two blurs | one light target, one small field texture; Compatibility renderer |

What HD-2D does that ADR-0039 chose not to do:
- the g-buffer and the screen-space shadow maps (extra passes, float targets);
- the bilateral blur.

Those stay out of the L1 option. Three of its ideas fit the tile field as it
is.

## Three follow-ups

1. **Light incidence on wall faces.** Today a wall face reads the field just
   in front of it, so a torch beside the wall and a torch facing it light the
   face the same. Give each face column a cosine term: the face's normal (+Y
   for a wall along X, +X for one along Y, as `WallFaces` already knows) dotted
   with the direction to each light, kept in the field as a second, per-face
   accumulation, or as the light's position carried to the shader for the few
   nearest lights.
   - Done when a torch beside a wall rakes across it, brighter toward the
     torch, while a torch in front lights it evenly. A still of each is the
     proof; the budget line shows the added cost.
   - This is HD-2D's N·L without a g-buffer: the surface type is already known
     per sprite on the tile grid.
2. **Wall height spans and light height.**
   - Today a blocker has a top Z and a light passes over it when it starts
     above the top. Edges should carry a Z span (base and top, from tiledata
     height).
   - A light should cast from a lift by kind: a wall torch or lamp post above
     its tile, a carried light at hand height, a light on a wall's tile pushed
     off the wall.
   - The ray's height should run from the light to the target's ground. Then
     a low wall, a fence or a window sill stops a floor-level glow but not a
     lamp post's, and an upper-storey wall stops nothing at street level.
   - Add HD-2D's see-through rule: tiledata names such as portcullis, bars and
     grate let light through.
   - Done when the probe shows a fence blocking a ground light and passing a
     raised one, and a still shows a lamp post lighting over a fence. Reach
     maps stay cached; the cache key gains the lift.
3. **Lamp shadows of mobiles and tall statics on the ground.** LIGHTS-05
   stamps a capsule shadow of each mobile away from the sun. Do the same away
   from each of the few nearest lamps:
   - the capsule's length comes from the lamp's lift and the caster's
     height, with HD-2D's soft knee so it stays finite;
   - its strength falls with the lamp's contribution at the caster;
   - trees and lamp posts cast the same way from their tile.
   This gives the "player's shadow turning to follow each light" of the
   demo plan's "into the building" scene without shadow maps. It costs a few
   capsule stamps per lamp, cap 4 lamps per caster.
   - Done when a clip of a walker passing between two torches shows two
     shadows swinging, and the budget stays under 8 ms at the bank view.

Each is one story under ADR-0039, inside the existing option or its UO+ flags,
with a probe check and before/after stills.
