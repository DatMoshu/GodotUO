// One sprite quad of the world depth pass, as an instance of a MultiMesh per
// atlas page (ADR-0007, GPU-01/02). Included by world_sprite_opaque.gdshader
// (the opaque pass: persistent chunk instances and queued sprites with a
// unique key) and world_sprite_blend.gdshader (the ordered pass: blends and
// later holders of an equal key, drawn in submission order by render
// priority).
//
// THE INSTANCE
//
// The instance carries no real transform. WorldDepthPass packs the twenty
// floats (12 transform, 4 colour, 4 custom) as follows:
//
//   MODEL_MATRIX[0].xy .. [3].xy   the quad's four corners in world pixels:
//                                  top-left, top-right, bottom-left,
//                                  bottom-right, exactly as the batcher
//                                  computes them for the canvas
//   MODEL_MATRIX[0].z              the absolute depth part (WorldDepthKey.DepthAbs)
//   MODEL_MATRIX[1].z              the absolute tie part (WorldDepthKey.TieAbs)
//   MODEL_MATRIX[2].z              (x + y) | (z + 128) << 14
//   MODEL_MATRIX[3].z              (x - y + 8192) | flags << 14
//   COLOR                          hue index, mode byte, alpha, raw key
//   INSTANCE_CUSTOM                the source rect: in texels when flagged,
//                                  else normalised; a negative size mirrors
//
// Every integer lane is an exact float (all are under 2^24).
//
// Flags: bit 0 the key is the integer one (else COLOR.a is a raw 0..1 key,
// the depth probe's step-1 path); bit 1 the tile walk's pixel cull applies
// (persistent statics, which the walk does not visit); bit 2 the source rect
// is in texels and takes the canvas renderer's texpixel_size product; bit 3
// the corners are absolute world pixels and draw_offset is subtracted
// (persistent statics, written once for every frame's offset).
//
// THE HUE
//
// uo_hue_core.gdshaderinc hues exactly as the canvas does; it reads the hue
// from an 8-bit packed colour, so the three values are packed here the way
// UltimaBatcher2D.Encode packs them, and unpack to the same bytes.

#include "res://src/Render/shaders/uo_hue_core.gdshaderinc"
#include "res://src/Render/shaders/world_pass.gdshaderinc"

uniform sampler2D page : filter_nearest, repeat_disable;
// 1 / width, 1 / height of the page, as the canvas renderer computes them.
uniform vec2 page_pixel = vec2(1.0, 1.0);
// 1: write the texel's sRGB bytes as they are (an HDR viewport keeps them;
// the composite puts them back untouched). 0: decode to linear so the
// renderer's encode on output gives the bytes back. The parity probe picks.
uniform float raw_output = 0.0;

// The instance's clip position, texture coordinate, packed hue and depth.
// Godot lets only vertex() itself read VERTEX, MODEL_MATRIX, COLOR and
// INSTANCE_CUSTOM, so each shader's vertex() passes them in and assigns the
// outputs to POSITION, UV, packed_hue, land_normal and fragment_depth.
void world_sprite_vertex(vec2 base, mat4 model, vec4 color, vec4 custom,
		out vec4 position, out vec2 uv, out vec4 hue, out float depth) {
	vec2 p0 = model[0].xy;
	vec2 p1 = model[1].xy;
	vec2 p2 = model[2].xy;
	vec2 p3 = model[3].xy;
	// base is exactly 0 or 1 per axis, so each mix returns a corner exactly.
	vec2 p = mix(mix(p0, p1, base.x), mix(p2, p3, base.x), base.y);

	uint packed_dx = uint(model[3].z);
	uint flags = packed_dx >> 14u;
	bool integer_key = (flags & 1u) != 0u;
	bool cull = (flags & 2u) != 0u;
	bool texel_rect = (flags & 4u) != 0u;
	bool absolute = (flags & 8u) != 0u;
	if (absolute) {
		p -= draw_offset;
	}

	vec4 src = texel_rect ? custom * page_pixel.xyxy : custom;
	uv = src.xy + src.zw * base;

	float index = color.r;
	hue = vec4(floor(index / 256.0) / 255.0, mod(index, 256.0) / 255.0, color.g / 255.0, color.b);

	bool placed = true;
	if (integer_key) {
		uint packed_sz = uint(model[2].z);
		int s = int(packed_sz & 16383u);
		if (cull) {
			int z = int(packed_sz >> 14u) - 128;
			int diff = int(packed_dx & 16383u) - 8192;
			vec2 tile_screen = vec2(float(diff * 22 - 22), float(s * 22 - z * 4 - 22));
			placed = !world_culled(tile_screen);
		}
		placed = placed && world_depth(uint(model[0].z), uint(model[1].z), s, depth);
	} else {
		float key = color.a;
		depth = key_reversed > 0.5 ? key : 1.0 - key;
	}
	position = placed ? world_clip(p, depth) : world_collapse();
}

// Premultiplied, as uo_shade returns it (upstream's pixel shader output).
vec4 world_sprite_shade(vec2 uv, vec2 screen_uv, vec2 viewport_size) {
	vec4 c = uo_shade(texture(page, uv), screen_uv, 1.0 / viewport_size);
	if (raw_output < 0.5) {
		c.rgb = world_srgb_to_linear(c.rgb);
	}
	return c;
}
