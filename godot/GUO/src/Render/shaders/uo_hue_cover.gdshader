// uo_hue.gdshader that can also draw covering land (--shared-cover).
//
// Covering land (UltimaBatcher2D.FlushCover) is a mesh over LandPages'
// Texture2DArray, drawn with uo_hue_land_array.gdshader. Under its own
// material it needs a canvas item of its own, two cuts a run: at the bank
// 1,572 of 1,601 items a frame. Under this one it is a command on the sprite
// item between the sprites around it, so painter order is unchanged.
//
// What tells the two apart has to be defined for every command, rects
// included, and CUSTOM0 is only defined on a mesh. The cover mesh is drawn
// with a marker texture of a reserved size (2 x 4096; both sides
// powers of two, so TEXTURE_PIXEL_SIZE is exact); only then is CUSTOM0 read,
// as uo_hue_land_array.gdshader reads it: normal in xyz, page in w. For
// everything else this is uo_hue.gdshader line for line. The batcher routes
// real sprite textures of this size through the original sprite material.

shader_type canvas_item;
render_mode blend_premul_alpha, unshaded;

#include "uo_hue_core.gdshaderinc"

uniform sampler2DArray land_pages : filter_nearest, repeat_disable;

varying flat float layer;
varying flat float cover;

void vertex() {
	packed_hue = COLOR;
	cover = TEXTURE_PIXEL_SIZE == vec2(0.5, 1.0 / 4096.0) ? 1.0 : 0.0;
	if (cover > 0.5) {
		land_normal = CUSTOM0.xyz;
		layer = CUSTOM0.w;
	} else {
		// Upstream's batcher gives every sprite the flat normal (0, 0, 1).
		land_normal = vec3(0.0, 0.0, 1.0);
		layer = 0.0;
	}
}

void fragment() {
	vec4 texel = cover > 0.5 ? texture(land_pages, vec3(UV, layer)) : texture(TEXTURE, UV);
	COLOR = uo_shade(texel, SCREEN_UV, SCREEN_PIXEL_SIZE);
}
