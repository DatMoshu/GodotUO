// The ordinary sprite path: hue the texel, let the hardware blend it.
//
// All the hueing is in uo_hue_core.gdshaderinc, shared with the custom-blend
// variant next door. This file is only the choice of blend.
//
// blend_premul_alpha, not blend_mix. uo_shade returns `color * alpha`, exactly
// as upstream's IsometricWorld.fx does, which is a PREMULTIPLIED colour, and
// upstream draws it under XNA's BlendState.AlphaBlend -- (One,
// InverseSourceAlpha). blend_premul_alpha is that same pair. blend_mix is
// (SourceAlpha, InverseSourceAlpha), which would multiply by alpha a second
// time and quietly darken every translucent sprite in the client.

shader_type canvas_item;
render_mode blend_premul_alpha, unshaded;

#include "uo_hue_core.gdshaderinc"

void vertex() {
	// COLOR is still the untouched modulate here; by fragment() the
	// texture has already been multiplied in. See the include's header.
	packed_hue = COLOR;
	// Not a mesh, so no per-vertex normal and no land here.
	land_light = 1.0;
}

void fragment() {
	COLOR = uo_shade(texture(TEXTURE, UV), SCREEN_UV, SCREEN_PIXEL_SIZE);
}
