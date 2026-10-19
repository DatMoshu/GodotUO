// PORT DEVIATION (GUO): water render modes (ADR-0037). Not upstream's.
//
// Included by uo_hue_core. A Wet tile -- land in the chunk mesh or a static
// sprite in the sorted pass -- carries bit 6 of the mode byte (0x40, free:
// modes reach 30 and bit 7 is circle-of-transparency) when a water style is
// active. uo_shade hues and lights the tile's own art exactly as before, then
// hands the result here. With Classic (water_model 0) nothing sets the bit
// and this file never runs, which is the parity guarantee.
//
// WHERE A FRAGMENT IS
//
// Water is anchored to the world, not the screen, or it would swim with the
// camera. The batcher hands over one mapping from the target's pixels to
// world pixels -- the frame UpdateRealScreenPosition uses before the camera
// offset, in which a tile at (X, Y, Z) has its centre at
// ((X - Y) * 22, (X + Y) * 22 - 4 Z). The cell is found from that by undoing
// the projection at the water's height, which the mask holds per cell.
//
// THE MASK
//
// water_mask is a ring of 128 x 128 world cells around the player (cell x, y
// at texel x & 127, y & 127), rebuilt on the CPU by WaterMask:
//   R  1 when the cell has water (Wet land or a Wet static), else 0
//   G  the height of that water, z + 128, over 255
// Cells outside water_mask_origin .. + 128 are unknown and read as water, so
// a stale texel never draws foam.

const int WATER_BIT = 0x40;
const int WATER_UO = 1;

// 0 is Classic: the branch is never taken. 1 is the built-in "uo" model.
uniform int water_model = 0;

// Seconds, from the client's own clock so a still and a dump are repeatable.
uniform float water_time = 0.0;

// world_px = target_px * xy + zw
uniform vec4 water_target_to_world = vec4(1.0, 1.0, 0.0, 0.0);

uniform sampler2D water_mask : filter_nearest, repeat_enable;
uniform vec2 water_mask_origin = vec2(0.0, 0.0);

// The ripple field (WaterRipples): 256 x 256 texels over the 128 x 128 cells
// from water_ripples_origin, 2 x 2 a cell; R is the surface height, 0.5 flat.
// Not art, so it may filter; outside the window it reads flat.
uniform sampler2D water_ripples : filter_linear, repeat_disable;
uniform vec2 water_ripples_origin = vec2(-100000.0, -100000.0);

// The style's own numbers (IWaterStyle.Bind). Tint is the deep water colour
// the waves lean towards; params are speed, scale (1 / world px), wave
// strength and foam strength; params2 are glint strength, foam width (in
// cells, from the shore edge), brightness levels (0 = smooth) and ripple
// strength (0 = ripples ignored).
uniform vec4 water_tint = vec4(0.12, 0.24, 0.42, 1.0);
uniform vec4 water_params = vec4(1.0, 0.045, 0.22, 0.85);
uniform vec4 water_params2 = vec4(0.35, 0.22, 6.0, 0.0);

vec4 water_mask_at(vec2 cell) {
	vec2 rel = cell - water_mask_origin;
	if (rel.x < 0.0 || rel.y < 0.0 || rel.x >= 128.0 || rel.y >= 128.0) {
		return vec4(1.0, 123.0 / 255.0, 0.0, 1.0);
	}
	return texelFetch(water_mask, ivec2(mod(cell, 128.0)), 0);
}

// The world cell under a world pixel, and where in that cell's diamond it
// falls (-0.5 .. 0.5 along x and y). Two passes: guess the usual sea height
// (-5), then use the height the mask has for the cell that guess lands in.
vec2 water_cell(vec2 world, out vec2 frac_xy) {
	float z = -5.0;
	vec2 cell = vec2(0.0);
	vec2 xy = vec2(0.0);
	for (int i = 0; i < 2; i++) {
		float u = world.x / 22.0;
		float v = (world.y + 4.0 * z) / 22.0;
		xy = vec2((u + v) * 0.5, (v - u) * 0.5);
		cell = floor(xy + 0.5);
		vec4 m = water_mask_at(cell);
		if (m.r > 0.5) {
			z = floor(m.g * 255.0 + 0.5) - 128.0;
		}
	}
	frac_xy = xy - cell;
	return cell;
}

float water_hash(vec2 p) {
	return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}

float water_noise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	return mix(mix(water_hash(i), water_hash(i + vec2(1.0, 0.0)), f.x),
			mix(water_hash(i + vec2(0.0, 1.0)), water_hash(i + vec2(1.0, 1.0)), f.x), f.y);
}

// Two crossing swells with a little noise on them: a height and its slope.
float water_height(vec2 p, float t) {
	float h = sin(p.x * 1.7 + t * 1.3 + sin(p.y * 0.9 + t * 0.7) * 1.4);
	h += 0.7 * sin(p.y * 2.3 - t * 1.1 + sin(p.x * 1.1 - t * 0.5) * 1.2);
	h += 0.6 * (water_noise(p * 2.0 + vec2(t * 0.4, -t * 0.3)) - 0.5);
	return h;
}

// The ripple height at a point in cells, -1 .. 1, and its slope.
float water_ripple(vec2 xy) {
	vec2 rel = xy - water_ripples_origin + 0.5;
	if (rel.x < 0.5 || rel.y < 0.5 || rel.x > 127.5 || rel.y > 127.5) {
		return 0.0;
	}
	return texture(water_ripples, rel / 128.0).r * 2.0 - 1.0;
}

float water_foam(vec2 cell, vec2 f, vec2 world, float t) {
	float width = max(water_params2.y, 0.01);
	float foam = 0.0;
	// The four cells sharing an edge with this one, in cell space.
	foam = max(foam, (1.0 - step(0.5, water_mask_at(cell + vec2(1.0, 0.0)).r))
			* (1.0 - smoothstep(0.0, width, 0.5 - f.x)));
	foam = max(foam, (1.0 - step(0.5, water_mask_at(cell - vec2(1.0, 0.0)).r))
			* (1.0 - smoothstep(0.0, width, 0.5 + f.x)));
	foam = max(foam, (1.0 - step(0.5, water_mask_at(cell + vec2(0.0, 1.0)).r))
			* (1.0 - smoothstep(0.0, width, 0.5 - f.y)));
	foam = max(foam, (1.0 - step(0.5, water_mask_at(cell - vec2(0.0, 1.0)).r))
			* (1.0 - smoothstep(0.0, width, 0.5 + f.y)));
	// Broken up and breathing, so it reads as surf rather than an outline.
	float lace = water_noise(world * 0.18 + vec2(t * 0.6, t * 0.35));
	float pulse = 0.65 + 0.35 * sin(t * 1.6 + (cell.x + cell.y) * 0.9);
	return foam * step(0.38, lace * pulse + foam * 0.35);
}

uniform int water_debug = 0;

vec3 water_shade(vec3 base, vec2 target_px) {
	// GUO_WATER_DEBUG: 1 paints the water magenta, 2 shows the ripple field.
	if (water_debug == 1) {
		return vec3(1.0, 0.0, 1.0);
	}
	// Snap to the art's pixel grid so the water keeps the tiles' pixels at
	// any zoom (AGENTS.md rule 7: native art is never filtered).
	vec2 world = floor(target_px * water_target_to_world.xy + water_target_to_world.zw) + 0.5;
	float t = water_time * water_params.x;

	vec2 f;
	vec2 cell = water_cell(world, f);

	vec2 p = world * water_params.y;
	float e = 0.35;
	float h = water_height(p, t);
	float hx = water_height(p + vec2(e, 0.0), t) - h;
	float hy = water_height(p + vec2(0.0, e), t) - h;
	// Ripples tilt the surface along their slope, in cell space turned to
	// the screen's (a cell step along X is (+22, +22) px, along Y (-22, +22)).
	float rs = water_params2.w;
	float ripple = 0.0;
	if (rs > 0.0) {
		vec2 xy = cell + f;
		ripple = water_ripple(xy);
		float rx = water_ripple(xy + vec2(0.5, 0.0)) - ripple;
		float ry = water_ripple(xy + vec2(0.0, 0.5)) - ripple;
		hx += (rx - ry) * rs * 8.0;
		hy += (rx + ry) * rs * 8.0;
	}
	if (water_debug == 2) {
		return vec3(clamp(0.5 + ripple * 6.0, 0.0, 1.0));
	}
	vec3 n = normalize(vec3(-hx, -hy, 0.6));
	float light = dot(n, normalize(vec3(-0.4, -0.6, 0.7)));

	float shade = (light - 0.75) * water_params.z * 2.0;
	float levels = water_params2.z;
	if (levels > 0.0) {
		shade = floor(shade * levels + 0.5) / levels;
	}

	// The tile's own colour stays the base (hues, light and season survive);
	// the swell darkens it towards the tint and lifts the crests.
	vec3 c = base + shade * mix(base, water_tint.rgb, 0.5);

	// A ripple's crest is lighter and its trough darker, on its own finer
	// steps: folded into the swell's few levels a small ring rounds away.
	float ring = clamp(ripple * rs * 2.5, -0.5, 0.5);
	if (levels > 0.0) {
		ring = floor(ring * levels * 3.0 + 0.5) / (levels * 3.0);
	}
	// An absolute lift, not a share of the tile's colour: deep water is dark
	// and a share of it would not show.
	c += ring * vec3(0.30, 0.36, 0.40);
	float glint = smoothstep(0.93, 0.99, light) * water_params2.x;
	c += vec3(glint);

	float foam = water_foam(cell, f, world, t) * water_params.w;
	c = mix(c, vec3(0.86, 0.92, 0.95), foam);

	return clamp(c, 0.0, 1.0);
}
