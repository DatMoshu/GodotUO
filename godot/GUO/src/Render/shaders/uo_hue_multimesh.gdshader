// --multimesh-sprites (PERF-MM-01): uo_hue.gdshader, plus the atlas rect a
// MultiMesh instance carries.
//
// The batcher draws a run of upright sprites on one texture as one MultiMesh
// of a shared unit quad (UltimaBatcher2D.MultiMesh.cs). Each instance's
// transform places the quad, its colour is the packed hue exactly as a rect's
// modulate is (Godot multiplies the instance colour into COLOR before
// vertex()), and its custom data is the source rect in normalised UVs. A rect,
// a mesh or a triangle list drawn under this material has no instance and
// leaves INSTANCE_CUSTOM zero, so it takes uo_hue's path unchanged; that is
// what lets the fallbacks share the item without a cut.
//
// Only the default (premultiplied) blend has this variant; a custom or
// additive blend keeps its own material and the per-sprite path.

shader_type canvas_item;
render_mode blend_premul_alpha, unshaded;

#include "uo_hue_core.gdshaderinc"

void vertex() {
	// COLOR is still the untouched modulate here; by fragment() the
	// texture has already been multiplied in. See the include's header.
	packed_hue = COLOR;
	// Upstream's batcher gives every sprite the flat normal (0, 0, 1).
	land_normal = vec3(0.0, 0.0, 1.0);
	// The unit quad's UV is 0..1; an instance maps it onto its source rect.
	if (INSTANCE_CUSTOM.z != 0.0) {
		UV = INSTANCE_CUSTOM.xy + UV * INSTANCE_CUSTOM.zw;
	}
}

void fragment() {
	COLOR = uo_shade(texture(TEXTURE, UV), SCREEN_UV, SCREEN_PIXEL_SIZE);
}
