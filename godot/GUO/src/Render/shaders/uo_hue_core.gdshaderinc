// Ported from ClassicUO's IsometricWorld.fx (BSD-2-Clause), pixel shader path.
//
// Shared by uo_hue.gdshader and uo_hue_blend.gdshader. The two differ only in
// how the result reaches the framebuffer -- the first lets the hardware blend
// it, the second blends by hand because ClassicUO's effects ask for equations
// Godot has no fixed mode for. The hueing itself is identical, so it lives
// here and neither copy can drift from the other.
//
// ClassicUO hues a sprite by treating the source pixel's red channel as a
// brightness index into a 32-entry palette row, then looking that row up in
// hues.mul. This reproduces that exactly; the mode constants, the thresholds
// and the palette geometry are all upstream's values, not new ones.
//
// HOW THE HUE GETS HERE
//
// Upstream sends (hue, mode, alpha) as a per-vertex TEXCOORD1. A Godot canvas
// item has no custom vertex attribute, so ADR-0002 packs the same three values
// into the per-quad modulate colour. Two measured facts shape that packing --
// see launchers\dev\render_probe.bat and launchers\dev\batcher_probe.bat,
// which re-measure both:
//
//   * COLOR in fragment() has ALREADY been multiplied by the texture, so the
//     raw modulate is captured in vertex() and carried across in a varying.
//   * the channels are 8-bit, so a 12-bit hue index needs two of them. In one
//     channel it comes back off by up to 3 -- a silently wrong colour.
//
//   R  hue index >> 8        B  mode, circle-of-transparency in bit 7
//   G  hue index & 0xFF      A  alpha, 0..1
//
// NOT HANDLED HERE: LAND and LAND_COLOR. Both need a per-vertex normal for
// get_light(), which modulate has no room for and a canvas quad has no notion
// of. They belong to the world mesh (MeshLayer), which ADR-0002 leaves as its
// own decision. They are not silently ignored below -- they render magenta, so
// a mistake shows up in a screenshot instead of looking plausible.

const int NONE = 0;
const int HUED = 1;
const int PARTIAL_HUED = 2;
const int HUE_TEXT_NO_BLACK = 3;
const int HUE_TEXT = 4;
const int LAND = 5;
const int LAND_COLOR = 6;
const int SPECTRAL = 7;
const int SHADOW = 8;
const int LIGHTS = 9;
const int EFFECT_HUED = 10;
const int GUMP = 20;

const float HUE_ROWS = 1024.0;
const float HUE_COLUMNS = 16.0;
const float HUE_WIDTH = 32.0;
const float HUES_PER_TEXTURE = HUE_ROWS * HUE_COLUMNS;

// hues.mul as a texture: 16 palettes across, 1024 down, 32 entries each.
uniform sampler2D hue_texture : filter_nearest, repeat_disable;

// The coloured-light table, used only by the LIGHTS mode.
uniform sampler2D light_texture : filter_nearest, repeat_disable;

// Only the LAND path reads this (upstream get_light); kept so SetBrightlight
// has somewhere to put its value once the world mesh lands.
uniform float brightlight = 0.0;

uniform float circle_of_transparency_radius = 0.0;
uniform vec2 circle_of_transparency_center = vec2(0.0, 0.0);

varying vec4 packed_hue;

vec3 get_rgb(float gray, float hue) {
	float half_pixel_x = (1.0 / (HUE_COLUMNS * HUE_WIDTH)) * 0.5;
	float hue_column_width = 1.0 / HUE_COLUMNS;
	float hue_start = fract(hue / HUE_COLUMNS);

	float x_pos = hue_start + gray / HUE_COLUMNS;
	x_pos = clamp(x_pos, hue_start + half_pixel_x,
			hue_start + hue_column_width - half_pixel_x);

	float y_pos = mod(hue, HUES_PER_TEXTURE) / (HUES_PER_TEXTURE - 1.0);

	return texture(hue_texture, vec2(x_pos, y_pos)).rgb;
}

vec3 get_colored_light(float shader_index, float gray) {
	return texture(light_texture, vec2(gray, (shader_index - 0.5) / 63.0)).rgb;
}

// Each including shader assigns `packed_hue = COLOR;` in its own vertex(),
// rather than calling a helper here. Godot rejects a varying assigned from
// anywhere but vertex() or fragment() outright -- "Varying may not be
// assigned in the 'uo_vertex' function" -- so the one duplicated line is the
// price of sharing the rest of this file.

// Returns what upstream's pixel shader returns: `color * alpha`, which is a
// PREMULTIPLIED colour. Both callers must blend it as such -- upstream runs
// under XNA's BlendState.AlphaBlend, which is (One, InverseSourceAlpha).
vec4 uo_shade(vec4 color, vec2 screen_uv, vec2 screen_pixel_size) {
	if (color.a == 0.0) {
		discard;
	}

	// Unpack. The 8-bit channels arrive as n/255, so scaling by 255 and
	// rounding recovers the byte exactly.
	float hue = floor(packed_hue.r * 255.0 + 0.5) * 256.0
			+ floor(packed_hue.g * 255.0 + 0.5);
	int raw_mode = int(floor(packed_hue.b * 255.0 + 0.5));
	float alpha = packed_hue.a;

	// Bit 7 of the mode byte carries circle-of-transparency, which upstream
	// signals by adding 1.0 to the alpha component. Alpha has to stay inside
	// 0..1 to survive the channel, so the flag moved to the spare bit.
	bool use_trans = (raw_mode & 0x80) != 0;
	int mode = raw_mode & 0x7F;

	if (alpha == 0.0) {
		discard;
	}

	if (mode >= GUMP) {
		mode -= GUMP;

		if (color.r < 0.02) {
			hue = 0.0;
		}
	}

	if (mode == HUED || (mode == PARTIAL_HUED
			&& color.r == color.g && color.r == color.b)) {
		color.rgb = get_rgb(color.r, hue);
	} else if (mode == HUE_TEXT_NO_BLACK) {
		if (color.r > 0.04 || color.g > 0.04 || color.b > 0.04) {
			color.rgb = get_rgb(1.0, hue);
		}
	} else if (mode == HUE_TEXT) {
		// 31 is max red, so this selects the darkest pixel's colour in the hue.
		color.rgb = get_rgb(1.0, hue);
	} else if (mode == LAND || mode == LAND_COLOR) {
		// Mesh-only; see the header. Loud on purpose.
		color.rgb = vec3(1.0, 0.0, 1.0);
	} else if (mode == SPECTRAL) {
		alpha = 1.0 - (color.r * 1.5);
		color.rgb = vec3(0.0);
	} else if (mode == SHADOW) {
		alpha = 0.4;
		color.rgb = vec3(0.0);
	} else if (mode == LIGHTS) {
		color.rgb = get_colored_light(hue - 1.0, color.r);
	} else if (mode == EFFECT_HUED) {
		color.rgb = get_rgb(color.g, hue);
	}

	if (use_trans && circle_of_transparency_radius > 0.0) {
		float ratio = length(screen_uv * (1.0 / screen_pixel_size)
				- circle_of_transparency_center)
				/ circle_of_transparency_radius;

		if (ratio < 0.85) {
			discard;
		}

		if (ratio < 1.0) {
			float t = (ratio - 0.85) / 0.15;
			alpha *= t * t * t;

			if (alpha < 0.02) {
				discard;
			}
		}
	}

	return color * alpha;
}
