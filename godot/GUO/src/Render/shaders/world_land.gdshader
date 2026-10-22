shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_always, skip_vertex_transform, shadows_disabled, fog_disabled;

// Covering land in the world depth pass (ADR-0004's amendment of 2026-09-25,
// drawn through ADR-0007's depth buffer): the land tiles RenderLists queues
// over objects below them, one mesh a frame over LandPages' Texture2DArray,
// with the vertices MeshLayer baked (positions, half-pixel inset UVs, the
// packed hue and the lighting normal) exactly as --merged-cover draws them
// on the canvas (CoverRuns.Build), plus the tile's key in CUSTOM1.
//
//   VERTEX.xy   world pixels, the draw offset already applied
//   COLOR       the 8-bit packed hue (UltimaBatcher2D.Encode)
//   CUSTOM0     normal.xyz, atlas layer
//   CUSTOM1     absolute depth part, absolute tie part, x + y, unused

#include "res://src/Render/shaders/uo_hue_core.gdshaderinc"
#include "res://src/Render/shaders/world_pass.gdshaderinc"

uniform sampler2DArray land_pages : filter_nearest, repeat_disable;
uniform float raw_output = 0.0;

varying flat float layer;

void vertex() {
	packed_hue = COLOR;
	land_normal = CUSTOM0.xyz;
	layer = CUSTOM0.w;
	float depth;
	bool placed = world_depth(uint(CUSTOM1.x), uint(CUSTOM1.y), int(CUSTOM1.z), depth);
	fragment_depth = depth;
	POSITION = placed ? world_clip(VERTEX.xy, depth) : world_collapse();
}

void fragment() {
	vec4 c = uo_shade(texture(land_pages, vec3(UV, layer)), SCREEN_UV, 1.0 / VIEWPORT_SIZE);
	if (raw_output < 0.5) {
		c.rgb = world_srgb_to_linear(c.rgb);
	}
	ALBEDO = c.rgb;
	DEPTH = fragment_depth;
}
