shader_type canvas_item;
render_mode blend_premul_alpha, unshaded;

#include "uo_hue_core.gdshaderinc"

// The world mesh. Same hueing as uo_hue.gdshader -- the only difference is
// where the land light comes from, which is why this exists as its own file
// rather than as a branch: CUSTOM0 is only present on a mesh, and reading it
// on an item drawn as a textured rect is not defined. See ADR-0004.

void vertex() {
	// COLOR is still the untouched vertex colour here; by fragment() the
	// texture has already been multiplied in. See the include's header.
	packed_hue = COLOR;

	// Written on the CPU by MeshLayer, from the vertex normal upstream would
	// have sent as NORMAL0.
	land_light = CUSTOM0.r;
}

void fragment() {
	COLOR = uo_shade(texture(TEXTURE, UV), SCREEN_UV, SCREEN_PIXEL_SIZE);
}
