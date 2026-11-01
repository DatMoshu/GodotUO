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
const int WATER_PIXEL = 2;
const int WATER_CEL = 3;
const int WATER_REALISTIC = 4;
const int WATER_CLASSICPLUS = 5;

// 0 is Classic: the branch is never taken. 1 to 5 are the built-in models a
// style picks (WaterRender.Model): uo, pixel, cel, realistic, classicplus. Store packs set
// the numbers below for one of them and carry no shader code (ADR-0037
// decision 6).
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
// x: grain, the world pixels one water pixel covers (1 = the art's own grid).
uniform vec4 water_params3 = vec4(1.0, 0.0, 0.0, 0.0);

// ADR-0048: linear HDR speed-mode target, indexed in world-target pixels.
// This sampler contains light only; native artwork retains nearest sampling.
uniform bool water_environment = false;
uniform float water_daylight = 1.0;
uniform float water_wind = 0.0;
uniform bool water_has_light_target = false;
uniform sampler2D water_light_target : filter_linear, repeat_disable;
uniform vec4 water_light_bounds = vec4(0.0, 0.0, 1.0, 1.0);
uniform vec3 water_light_ambient = vec3(1.0);

// ADR-0048: geometric plane-consistency reflections, bound only for a valid frame.
#include "res://src/Render/SpeedMode/Weather/speed_reflection.gdshaderinc"
uniform bool water_has_reflections = false;
uniform sampler2D water_surface_buffer : filter_nearest, repeat_disable;
uniform sampler2D water_scene_color : filter_nearest, repeat_disable;
uniform vec2 water_reflection_target_size = vec2(1.0);
uniform vec2 water_reflection_origin = vec2(0.0);
uniform float water_reflection_art_scale = 1.0;

vec3 water_local_light(vec2 target_px) {
	if (!water_has_light_target) { return vec3(0.0); }
	vec2 uv = (target_px - water_light_bounds.xy) / max(water_light_bounds.zw, vec2(1.0));
	if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0)))) {
		return vec3(0.0);
	}
	return max(texture(water_light_target, uv).rgb - water_light_ambient, vec3(0.0));
}

vec4 water_mask_at(vec2 cell) {
	vec2 rel = cell - water_mask_origin;
	if (rel.x < 0.0 || rel.y < 0.0 || rel.x >= 128.0 || rel.y >= 128.0) {
		return vec4(1.0, 123.0 / 255.0, 0.0, 1.0);
	}
	return texelFetch(water_mask, ivec2(mod(cell, 128.0)), 0);
}

// A continuous wet-cell field for Lighting's shallow/deep colour. Sampling
// four discrete distant neighbours made the shelf jump at tile boundaries,
// most visibly around individual dock pilings. Only mask data is blended.
float water_wet_linear(vec2 xy) {
	vec2 lo = floor(xy);
	vec2 blend = fract(xy);
	return mix(mix(water_mask_at(lo).r, water_mask_at(lo + vec2(1.0, 0.0)).r, blend.x),
			mix(water_mask_at(lo + vec2(0.0, 1.0)).r,
			water_mask_at(lo + vec2(1.0, 1.0)).r, blend.x), blend.y);
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

// Lighting waves use continuous world-space directional derivatives. Value
// noise's square interpolation lattice became visible in specular normals,
// even though its coordinates were already world anchored. No tile UV or
// cell index participates in these slopes; older styles keep their field.
vec2 water_lighting_slope(vec2 p, float t) {
	// A slow continuous warp prevents stationary rows of interference nodes.
	vec2 wa = vec2(0.31, -0.17);
	vec2 wb = vec2(-0.23, 0.29);
	float ax = dot(p, wa) + t * 0.11;
	float ay = dot(p, wb) - t * 0.09;
	vec2 q = p + 0.25 * vec2(sin(ax), sin(ay));
	vec2 a = vec2(1.7, 0.65);
	vec2 b = vec2(-0.6, 2.3);
	vec2 c = vec2(4.1, 1.3);
	vec2 d = vec2(-2.7, 5.3);
	vec2 slope = cos(dot(q, a) + t * 1.3) * a
			+ 0.7 * cos(dot(q, b) - t * 1.1) * b
			+ 0.22 * cos(dot(q, c) + t * 2.1) * c
			+ 0.13 * cos(dot(q, d) - t * 1.7) * d;
	// Chain rule keeps these the analytic slopes of the warped waves.
	return vec2(dot(slope, vec2(1.0, 0.0) + 0.25 * vec2(cos(ax) * wa.x, cos(ay) * wb.x)),
			dot(slope, vec2(0.0, 1.0) + 0.25 * vec2(cos(ax) * wa.y, cos(ay) * wb.y)));
}

// The ripple height at a point in cells, -1 .. 1, and its slope.
float water_ripple(vec2 xy) {
	vec2 rel = xy - water_ripples_origin + 0.5;
	if (rel.x < 0.5 || rel.y < 0.5 || rel.x > 127.5 || rel.y > 127.5) {
		return 0.0;
	}
	return texture(water_ripples, rel / 128.0).r * 2.0 - 1.0;
}

// One neighbour's share of the shore for water_shore: dry, and its ground no
// more than 8 above this water, so a beach makes surf and a cliff does not.
float water_low_dry(vec4 m, float wz) {
	return (1.0 - step(0.5, m.r)) * step(floor(m.g * 255.0 + 0.5) - 128.0 - wz, 8.0);
}

// Reconstruct the low-shore mask between cell centres. The old max of four
// edge ramps restarted on each diamond, drawing ladder bars on diagonal
// coasts. The reconstructed mask is continuous across cell edges; its
// gradient converts the mask value into an approximate distance in cells.
// Height checks still exclude cliffs; the sampler itself stays nearest.
float water_shore(vec2 cell, vec2 f) {
	float width = max(water_params2.y, 0.01);
	vec2 xy = cell + f;
	vec2 lo = floor(xy);
	vec2 blend = fract(xy);
	vec4 ma = water_mask_at(lo);
	vec4 mb = water_mask_at(lo + vec2(1.0, 0.0));
	vec4 mc = water_mask_at(lo + vec2(0.0, 1.0));
	vec4 md = water_mask_at(lo + vec2(1.0, 1.0));
	// Shore art may overlap a dry cell; take the surface height from water,
	// never that dry cell's ground height (which would make cliffs low).
	float count = ma.r + mb.r + mc.r + md.r;
	float wz = floor((ma.g * ma.r + mb.g * mb.r + mc.g * mc.r + md.g * md.r)
			/ max(count, 1.0) * 255.0 + 0.5) - 128.0;
	float a = water_low_dry(ma, wz);
	float b = water_low_dry(mb, wz);
	float c = water_low_dry(mc, wz);
	float d = water_low_dry(md, wz);
	float dry = mix(mix(a, b, blend.x), mix(c, d, blend.x), blend.y);
	vec2 gradient = vec2(mix(b - a, d - c, blend.y), mix(c - a, d - b, blend.x));
	// Native shore sprites can extend into a mask cell classed as dry. Fade
	// on both sides of the contour, rather than filling that entire overlap.
	float slope = length(gradient);
	float distance_to_shore = abs(0.5 - dry) / max(slope, 0.001);
	return (1.0 - smoothstep(0.0, width, distance_to_shore)) * step(0.001, slope);
}

float water_foam(vec2 cell, vec2 f, vec2 world, float t) {
	float foam = water_shore(cell, f);
	// Broken up and breathing, so it reads as surf rather than an outline.
	float lace = water_noise(world * 0.18 + vec2(t * 0.6, t * 0.35));
	float pulse = 0.65 + 0.35 * sin(t * 1.6 + (cell.x + f.x + cell.y + f.y) * 0.9);
	return foam * smoothstep(0.35, 0.65, lace * pulse);
}

// Surf arrives in connected wavelets, not a solid line offset from the
// native shore sprite. The noise follows world cells across tile boundaries.
float water_surf(vec2 cell, vec2 f, float t) {
	float wavelet = water_noise((cell + f) * 2.2 + vec2(t * 0.16, -t * 0.1));
	return water_shore(cell, f) * smoothstep(0.42, 0.68, wavelet);
}

// The 4 x 4 ordered-dither threshold at a water pixel, 0 .. 15/16.
float water_bayer(vec2 px) {
	ivec2 i = ivec2(mod(px, 4.0));
	int b[16] = int[16](0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5);
	return float(b[i.y * 4 + i.x]) / 16.0;
}

// PIXEL: a 16-colour-era sea. Light falls in a few hard steps, dithered
// between them on the water's own pixel grid instead of rounded, the colour
// is cut to a short ramp, and surf and glints are whole pixels.
vec3 water_pixel(vec3 base, vec2 cell, vec2 f, vec2 world, float t, float light, float ripple, float rs) {
	vec2 px = floor(world / max(water_params3.x, 1.0));
	float d = water_bayer(px);
	float levels = max(water_params2.z, 1.0);
	float shade = (light - 0.75) * water_params.z * 2.0;
	shade = floor(shade * levels + d) / levels;
	vec3 c = base + shade * mix(base, water_tint.rgb, 0.5);
	// A share of the dark native texel is lost under the animated pattern.
	// Give the local wave its own absolute lift (as the UO model does), then
	// quantise it with the same palette and dither: rings, never white foam.
	float ring = clamp(ripple * rs * 3.5, -0.5, 0.5);
	c += max(ring, 0.0) * vec3(0.32, 0.40, 0.48);
	// Darken the trough only by a share of its native colour; subtracting
	// the absolute crest lift here would punch a black hole round a wader.
	c *= 1.0 + min(ring, 0.0) * 0.5;
	c = floor(c * 12.0 + d) / 12.0;
	// Only the steepest-lit few percent, and half of those by the dither:
	// single bright pixels, not patches (about 1% of the sea at glint 0.6).
	// The threshold alone still admits highlights at zero: explicitly honour
	// a pack disabling glints, without changing any positive setting.
	float glint = step(0.995 - water_params2.x * 0.005, light) * step(0.5, d)
			* sign(water_params2.x);
	c = mix(c, vec3(0.92, 0.96, 1.0), glint);
	float foam = step(d + 0.01, water_surf(cell, f, t) * water_params.w
			* (0.55 + 0.45 * sin(t * 1.6 + (cell.x + f.x + cell.y + f.y) * 0.9)));
	c = mix(c, vec3(0.9, 0.95, 1.0), foam);
	return clamp(c, 0.0, 1.0);
}

// CEL: flat colour in the style's tint, lit by the tile's own light so
// night still falls; a few bold light bands, white surf with a dark ink line
// on its water side, and ripples drawn as crisp rings.
vec3 water_cel(vec3 base, vec2 cell, vec2 f, float t, float light, float ripple, float rs) {
	float luma = dot(base, vec3(0.299, 0.587, 0.114));
	vec3 flat_c = water_tint.rgb * (0.55 + 1.6 * luma);
	float levels = max(water_params2.z, 2.0);
	float band = floor(clamp((light - 0.55) * water_params.z * 4.0 + 0.5, 0.0, 0.999) * levels) / (levels - 1.0);
	// The light band sits one step above the base, not two: next to UO's
	// art a brighter one read cartoon-loud.
	vec3 c = flat_c * (0.8 + 0.3 * band);
	// A ripple is drawn as its contour lines, light on the crest and dark in
	// the trough: thin rings, where a band of heights filled a disc.
	float r = ripple * rs;
	c = mix(c, flat_c * 1.5 + 0.06, step(abs(r - 0.1), 0.02));
	c = mix(c, flat_c * 0.6, step(abs(r + 0.1), 0.02));
	c += vec3(step(0.985, light) * water_params2.x);
	float edge = water_surf(cell, f, t) * (0.85 + 0.15 * sin(t * 1.6 + (cell.x + f.x + cell.y + f.y) * 0.9));
	float on = step(0.01, water_params.w);
	float foam = step(0.5, edge) * on;
	float ink = step(0.3, edge) * (1.0 - foam) * on;
	c = mix(c, flat_c * 0.35, ink);
	c = mix(c, vec3(0.95, 0.98, 1.0), foam);
	return clamp(c, 0.0, 1.0);
}

// REALISTIC: smooth light, no steps; open water deepens towards the tint
// away from the shore, the sun's highlight is sharp with a glitter of small
// sparkles, and surf is soft. On Compatibility the glitter is left out, with
// the chop above.
vec3 water_realistic(vec3 base, vec2 cell, vec2 f, vec2 world, float t, float light, float ripple, float rs) {
	float edge = water_shore(cell, f);
	float open_w = water_mask_at(cell + vec2(2.0, 0.0)).r * water_mask_at(cell - vec2(2.0, 0.0)).r
			* water_mask_at(cell + vec2(0.0, 2.0)).r * water_mask_at(cell - vec2(0.0, 2.0)).r;
	float luma = dot(base, vec3(0.299, 0.587, 0.114));
	vec3 deep = water_tint.rgb * (0.5 + 1.2 * luma);
	vec3 c = mix(base, deep, 0.35 + 0.35 * open_w * (1.0 - edge));
	c *= 1.0 + (light - 0.75) * water_params.z * 2.4;
	c += ripple * rs * vec3(0.50, 0.58, 0.64);
	float spec = pow(clamp(light, 0.0, 1.0), 48.0) * water_params2.x * 2.0;
#if CURRENT_RENDERER != RENDERER_COMPATIBILITY
	float sparkle = step(0.94, water_noise(world * 0.5 + vec2(t * 2.3, -t * 1.7))) * step(0.9, light);
	spec += sparkle * water_params2.x;
#endif
	c += vec3(spec) * (0.4 + 1.6 * luma);
	float lace = water_noise(world * 0.12 + vec2(t * 0.5, t * 0.3));
	float foam = water_surf(cell, f, t) * smoothstep(0.3, 0.7, lace + edge * 0.4) * water_params.w;
	c = mix(c, vec3(0.88, 0.93, 0.96), foam);
	return clamp(c, 0.0, 1.0);
}

// Lighting is a separate branch so old model arithmetic remains unchanged.
vec3 water_realistic_lighting(vec3 base, vec2 cell, vec2 f, vec2 world, float t, float light, float ripple, float rs, vec2 target_px) {
	float edge = water_shore(cell, f);
	float open_w = water_wet_linear(cell + f);
	float luma = dot(base, vec3(0.299, 0.587, 0.114));
	vec3 deep = water_tint.rgb * (0.5 + 1.2 * luma);
	// Shore distance is an approximation from the existing wet-cell mask,
	// not terrain bathymetry. Keep a lighter shelf near land and deepen the
	// open-water colour without another pass or more texture fetches.
	vec3 shallow = mix(base, vec3(0.10, 0.30, 0.32) * (0.5 + 1.2 * luma), 0.5);
	float depth = clamp(open_w - edge * 0.65, 0.0, 1.0);
	vec3 c = mix(base, mix(shallow, deep, depth * 0.72), 0.25);
	// Keep native daytime blue and brightness. Depth may gently darken it;
	// night brightness still comes from the ordinary world light target.
	float native_luma = luma * (1.0 - depth * 0.05);
	c *= native_luma / max(dot(c, vec3(0.299, 0.587, 0.114)), 0.001);
	// Broad swell normals should not paint dark bands over native water.
	// Keep their modulation subtle; crests, ripples and local glints remain.
	c *= clamp(1.0 + (light - 0.75) * water_params.z * 0.6, 0.94, 1.035);
	c += ripple * rs * vec3(0.50, 0.58, 0.64);
	// Calm water has sparse, soft crests; stronger shared wind admits more.
	float crest = pow(clamp(light, 0.0, 1.0), 96.0);
	float spec = crest * water_params2.x * 0.7
			* (0.45 + 0.55 * min(abs(water_wind), 1.0));
	if (water_environment) {
		// Sky colour follows shard daylight, not wall-clock time. Final world
		// lighting supplies brightness, so only a restrained colour shift here.
		vec3 sky = mix(vec3(0.48, 0.61, 0.92), vec3(0.84, 0.94, 1.0), water_daylight);
		c *= mix(vec3(1.0), sky, 0.3);
		spec *= 0.12 + 0.88 * water_daylight;
		// Approximate broken reflections of local lights. The occluded field
		// supplies colour/reach; moving wavelets break it into surface streaks.
		// This is neither a mirrored scene reflection nor ray tracing.
		// Two irregular, stretched fields vary length and spacing without a
		// periodic row phase. The slow field also varies the glint threshold.
		float scatter = water_noise(world * 0.019 + vec2(t * 0.07, -t * 0.04));
		vec2 drift = vec2(scatter * 1.8 - t * 0.2, t * 0.6);
		float short_glints = smoothstep(0.61 + scatter * 0.12, 0.84,
				water_noise(world * vec2(0.11, 0.39) + drift));
		float long_glints = smoothstep(0.72, 0.92,
				water_noise(world * vec2(0.047, 0.23) - drift * 0.7));
		float flecks = smoothstep(0.82, 0.96, water_noise(world * 0.43 + vec2(t * 0.3)));
		c += water_local_light(target_px) * (0.06 + short_glints * 0.70
				+ long_glints * 0.35 + flecks * 0.10) * water_params2.x;
	}
	c += vec3(spec) * (0.4 + 1.6 * luma);
	float lace = water_noise(world * 0.12 + vec2(t * 0.5, t * 0.3));
	float foam = water_surf(cell, f, t) * smoothstep(0.3, 0.7, lace + edge * 0.4) * water_params.w;
	vec3 foam_color = vec3(0.88, 0.93, 0.96);
	if (water_environment) { foam_color *= mix(vec3(0.45, 0.57, 0.8), vec3(1.0), water_daylight); }
	c = mix(c, foam_color, foam);
	return clamp(c, 0.0, 1.0);
}

uniform int water_debug = 0;

vec3 water_shade(vec3 base, vec2 target_px) {
	// GUO_WATER_DEBUG: 1 paints the water magenta, 2 shows the ripple field.
	if (water_debug == 1) {
		return vec3(1.0, 0.0, 1.0);
	}
	// Snap to the art's pixel grid so the water keeps the tiles' pixels at
	// any zoom (AGENTS.md rule 7: native art is never filtered).
	// A coarser grain makes bigger water pixels, still on whole art pixels.
	float grain = max(water_params3.x, 1.0);
	vec2 world = (floor((target_px * water_target_to_world.xy + water_target_to_world.zw) / grain) + 0.5) * grain;
	float t = water_time * water_params.x;

	vec2 f;
	vec2 cell = water_cell(world, f);

	if (water_model == WATER_CLASSICPLUS) {
		// Keep the actual, already hued/lit native texel. No replacement ramp,
		// white foam or glints: a one-step shimmer dithered on the world grid
		// leaves UO's own animated pattern and palette doing the work.
		float phase = sin(dot(world, vec2(0.017, 0.011)) - t);
		float ripple = water_ripple(cell + f) * water_params2.w;
		float lap = water_shore(cell, f) * sin(t * 0.7 + dot(world, vec2(0.013, 0.009)));
		float shift = phase * water_params.z + ripple * 0.12 + lap * water_params.w;
		float threshold = water_bayer(floor(world / grain));
		float step_count = floor(abs(shift) * 8.0 + threshold);
		return clamp(base + sign(shift) * step_count * base / 16.0, 0.0, 1.0);
	}

	vec2 p = world * water_params.y;
	if (water_environment) {
		p += vec2(water_wind * t * 0.22, -abs(water_wind) * t * 0.08);
	}
	float e = 0.35;
	float hx;
	float hy;
	if (water_environment) {
		vec2 slope = water_lighting_slope(p, t) * e;
		float wind_strength = 0.65 + abs(water_wind) * 0.7;
		hx = slope.x * wind_strength;
		hy = slope.y * wind_strength;
	} else {
		float h = water_height(p, t);
		hx = water_height(p + vec2(e, 0.0), t) - h;
		hy = water_height(p + vec2(0.0, e), t) - h;
		if (water_model == WATER_REALISTIC) {
		// Finer chop on the swell. Compatibility (the web build) skips it:
		// three more noise lookups a fragment are too dear there.
#if CURRENT_RENDERER != RENDERER_COMPATIBILITY
		vec2 q = p * 3.1 + vec2(t * 0.9, t * 0.6);
		float chop = water_noise(q) - 0.5;
		hx += (water_noise(q + vec2(e * 3.1, 0.0)) - 0.5 - chop) * 0.5;
		hy += (water_noise(q + vec2(0.0, e * 3.1)) - 0.5 - chop) * 0.5;
#endif
		}
	}
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

	if (water_model == WATER_PIXEL) {
		return water_pixel(base, cell, f, world, t, light, ripple, rs);
	}
	if (water_model == WATER_CEL) {
		return water_cel(base, cell, f, t, light, ripple, rs);
	}
	if (water_model == WATER_REALISTIC) {
		if (water_environment) {
			vec3 c = water_realistic_lighting(base, cell, f, world, t, light, ripple, rs, target_px);
			if (water_has_reflections) {
				vec2 uv = target_px / water_reflection_target_size;
				vec4 receiver = texture(water_surface_buffer, uv);
				if ((int(round(receiver.a)) & 15) == 6 && (int(round(receiver.a)) & 16) == 0) {
					vec4 reflection = speed_reflection(water_surface_buffer, water_scene_color,
						uv, water_reflection_target_size, receiver.xyz, water_reflection_origin, water_reflection_art_scale, water_time);
					c = mix(c, reflection.rgb, reflection.a * 0.35);
				}
			}
			return c;
		}
		return water_realistic(base, cell, f, world, t, light, ripple, rs);
	}

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
