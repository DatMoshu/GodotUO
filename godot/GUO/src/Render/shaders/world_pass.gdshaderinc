// The world depth pass (ADR-0007, amendment 2026-10-19, key layout 2026-10-22):
// what every quad in the pass shares. Included by world_sprite_opaque,
// world_sprite_blend and world_land.
//
// A quad is placed in the world render target's own pixels through the same
// Transform2D the canvas world pass puts on its canvas item, operation for
// operation as renderer_rd/shaders/canvas.glsl does it (Godot 4.7.2): the
// item transform and the screen transform as mat4 products. A pixel centre
// exactly on a quad edge (zoom 1.25 and 2.5 put integer positions on halves
// all the time) is decided by these roundings, so they must be the same ones.
//
// Its depth is the frame-relative key (world_depth_key.gdshaderinc), carried
// flat to an unconditional fragment DEPTH write. The key is rebuilt from the
// instance's absolute parts in uint arithmetic; nothing converts it to float
// numerically. Reverse Z: a larger key is nearer.

#include "res://src/Render/shaders/world_depth_key.gdshaderinc"

uniform vec2 view_x = vec2(1.0, 0.0);
uniform vec2 view_y = vec2(0.0, 1.0);
uniform vec2 view_origin = vec2(0.0);
// The canvas screen transform: scale 2 / size, offset -(size / 2) * scale,
// both rounded to float on the CPU as the canvas renderer does.
uniform vec4 screen = vec4(1.0, 1.0, 0.0, 0.0);
// Clip y sign; the depth probe measured -1 on Forward+.
uniform float flip_y = -1.0;
// 1: clip z is the key (reverse Z, larger nearer); 0: 1 - key.
uniform float key_reversed = 1.0;
// The frame's origins: WorldDepthKey.DepthOrigin and the smallest (x - y)
// the screen's left edge admits (GameScene hands both over per frame).
uniform int depth_origin = 0;
uniform int tie_min_diff = -100000;
// The tile walk's pixel cull, in world pixels before the draw offset:
// (min x, min y, max x, max y). An instance flagged for the cull collapses
// outside it, exactly as AddTileToRenderList skips the object.
uniform vec4 cull_rect = vec4(-1e9, -1e9, 1e9, 1e9);
// The draw offset (GameScene._offset) a persistent instance subtracts from
// its absolute corners, as UpdateRealScreenPosition subtracts it in integer
// arithmetic: both sides are integers under 2^24, so the float difference is
// exact. A captured sprite already has it applied and carries no flag.
uniform vec2 draw_offset = vec2(0.0);

varying flat float fragment_depth;

// Clip position for a point in world pixels.
vec4 world_clip(vec2 p, float depth) {
	mat4 model_matrix = mat4(vec4(view_x, 0.0, 0.0), vec4(view_y, 0.0, 0.0), vec4(0.0, 0.0, 1.0, 0.0), vec4(view_origin, 0.0, 1.0));
	vec2 vertex = (model_matrix * vec4(p, 0.0, 1.0)).xy;
	mat4 screen_transform = mat4(vec4(screen.x, 0.0, 0.0, 0.0), vec4(0.0, screen.y, 0.0, 0.0), vec4(0.0, 0.0, 1.0, 0.0), vec4(screen.zw, 0.0, 1.0));
	vec2 ndc = (screen_transform * vec4(vertex, 0.0, 1.0)).xy;
	ndc.y *= -flip_y;
	return vec4(ndc, depth, 1.0);
}

// A quad the frame cannot place: outside the clip volume on every vertex,
// so nothing is rasterised and no depth is written.
vec4 world_collapse() {
	return vec4(2.0, 2.0, 2.0, 1.0);
}

// The depth for an absolute key; false collapses the quad.
bool world_depth(uint depth_abs, uint tie_abs, int s, out float depth) {
	uint key;
	if (!world_depth_relative(depth_abs, tie_abs, s, uint(depth_origin), tie_min_diff, key)) {
		depth = 0.0;
		return false;
	}
	float encoded = world_depth_key(key);
	depth = key_reversed > 0.5 ? encoded : 1.0 - encoded;
	return true;
}

bool world_culled(vec2 tile_screen) {
	return tile_screen.x < cull_rect.x || tile_screen.x > cull_rect.z
		|| tile_screen.y < cull_rect.y || tile_screen.y > cull_rect.w;
}

// sRGB to linear, exactly, so the renderer's encode on output gives the
// texel's bytes back (the canvas writes them untouched). Measured by the
// depth probe: zero wrong pixels on 8-bit art.
vec3 world_srgb_to_linear(vec3 c) {
	return mix(pow((c + vec3(0.055)) * (1.0 / 1.055), vec3(2.4)), c * (1.0 / 12.92), lessThan(c, vec3(0.04045)));
}
