// The additive sprite path: hue the texel, let the hardware add it.
//
// All the hueing is in uo_hue_core.gdshaderinc, shared with the other
// variants next door. This file is only the choice of blend.
//
// XNA's BlendState.Additive is (SourceAlpha, One) on colour and on alpha,
// which is Godot's blend_add exactly, so it needs none of uo_hue_blend's
// back-buffer machinery. That machinery is also wrong for it: every sprite
// drawn after one copy reads the same destination, so sprites that overlap
// replace each other instead of adding up. The light pass is hundreds of
// overlapping additive sprites (GameScene.PrepareLightsRendering), and lit
// only as brightly as its last light wherever two met (parity night
// 2026-09-26, the Volcano).

shader_type canvas_item;
render_mode blend_add, unshaded;

#include "uo_hue_core.gdshaderinc"

void vertex() {
	// COLOR is still the untouched modulate here; by fragment() the
	// texture has already been multiplied in. See the include's header.
	packed_hue = COLOR;
	// Upstream's batcher gives every sprite the flat normal (0, 0, 1).
	land_normal = vec3(0.0, 0.0, 1.0);
}

void fragment() {
	COLOR = uo_shade(texture(TEXTURE, UV), SCREEN_UV, SCREEN_PIXEL_SIZE);
}
