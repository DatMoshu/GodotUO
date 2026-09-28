shader_type canvas_item;
render_mode blend_premul_alpha, unshaded;

#include "uo_hue_core.gdshaderinc"

// The world mesh's land, drawn as one mesh across all its atlas pages
// (--merged-land=array, Epic B; LandPages.cs). Everything is uo_hue_mesh's
// but the texture: the pages are layers of one array, and the layer rides in
// the fourth component of CUSTOM0, which the land normal leaves unused.
// Nearest sampling, project rule 7.

uniform sampler2DArray land_pages : filter_nearest, repeat_disable;

varying flat float layer;

void vertex() {
	packed_hue = COLOR;
	land_normal = CUSTOM0.xyz;
	layer = CUSTOM0.w;
}

void fragment() {
	COLOR = uo_shade(texture(land_pages, vec3(UV, layer)), SCREEN_UV, SCREEN_PIXEL_SIZE);
}
