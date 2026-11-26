// Multiply the destination RGBA by (1 - source alpha).
// Aura.Draw pairs this with a source-alpha additive pass (ADR-0003).
shader_type canvas_item;
render_mode blend_mul, unshaded;

#include "uo_hue_core.gdshaderinc"

void vertex() {
	packed_hue = COLOR;
	land_normal = vec3(0.0, 0.0, 1.0);
}

void fragment() {
	vec4 src = uo_shade(texture(TEXTURE, UV), SCREEN_UV, SCREEN_PIXEL_SIZE);
	COLOR = vec4(1.0 - src.a);
}
