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
// water_mask is a ring of N x N world cells around the player (cell x, y at
// texel x mod N, y mod N; N is the texture's own size, 256 from WaterMask),
// rebuilt on the CPU by WaterMask:
//   R  1 when the cell has water (Wet land or a Wet static), else 0
//   G  the height of that water, z + 128, over 255
//   B  shore distance of a wet cell: cells to the nearest dry cell along the
//      eight directions, capped at 5, as distance / 5 (0 for dry cells)
//   A  255 - (long shore distance * 8 + post * 4): the Chebyshev distance to
//      the nearest dry cell, capped at 30, and whether a submerged dock post
//      stands in the cell (water_mask_far, water_mask_post)
// Cells outside water_mask_origin .. + N are unknown and read as deep water,
// so a stale texel never draws foam.

const int WATER_BIT = 0x40;
const int WATER_UO = 1;
const int WATER_PIXEL = 2;
const int WATER_CEL = 3;
const int WATER_REALISTIC = 4;
uniform bool water_harbor = false;
uniform bool water_harbor_swell = false;
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
	float n = float(textureSize(water_mask, 0).x);
	if (rel.x < 0.0 || rel.y < 0.0 || rel.x >= n || rel.y >= n) {
		return vec4(1.0, 123.0 / 255.0, 1.0, 15.0 / 255.0);
	}
	return texelFetch(water_mask, ivec2(mod(cell, n)), 0);
}

// Mask A unpacked: the long shore distance in cells (0 dry .. 30), and the post flag.
float water_mask_far(vec4 m) {
	return floor((255.0 - floor(m.a * 255.0 + 0.5)) / 8.0);
}

bool water_mask_post(vec4 m) {
	return mod(255.0 - floor(m.a * 255.0 + 0.5), 8.0) >= 4.0;
}

// The long shore distance between cell centres, in cells: the same four-cell
// blend as water_wet_linear, so it has no step at a tile boundary.
float water_far_linear(vec2 xy) {
	vec2 lo = floor(xy);
	vec2 blend = fract(xy);
	return mix(mix(water_mask_far(water_mask_at(lo)), water_mask_far(water_mask_at(lo + vec2(1.0, 0.0))), blend.x),
			mix(water_mask_far(water_mask_at(lo + vec2(0.0, 1.0))),
			water_mask_far(water_mask_at(lo + vec2(1.0, 1.0))), blend.x), blend.y);
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
	if (water_harbor) {
		// A crest only catches the sun where the small crossing ripples agree.
		float fleck = water_noise(world * vec2(0.16, 0.37) + vec2(t * 0.34, -t * 0.21));
		float spec = pow(clamp(light, 0.0, 1.0), 64.0) * smoothstep(0.59, 0.89, fleck);
		c += vec3(0.76, 0.87, 0.94) * spec * min(water_params2.x, 2.0) * 0.27;
	} else {
		float spec = pow(clamp(light, 0.0, 1.0), 48.0) * water_params2.x * 2.0;
#if CURRENT_RENDERER != RENDERER_COMPATIBILITY
		float sparkle = step(0.94, water_noise(world * 0.5 + vec2(t * 2.3, -t * 1.7))) * step(0.9, light);
		spec += sparkle * water_params2.x;
#endif
		c += vec3(spec) * (0.4 + 1.6 * luma);
	}
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
	// A local disturbance (a swimmer's ring, a wake) reads at the default ripple strength: small
	// heights pass through unchanged, so still water is untouched; larger ones are lifted, capped.
	float disturb = ripple * rs;
	disturb = clamp(disturb * (1.0 + 1.5 * smoothstep(0.015, 0.1, abs(disturb))), -0.16, 0.16);
	c += disturb * vec3(0.50, 0.58, 0.64);
	// Calm water has sparse, soft crests; stronger shared wind admits more.
	float crest = pow(clamp(light, 0.0, 1.0), 96.0);
	float crest_gain = water_params2.x * 0.7;
	if (water_harbor) {
		float fleck = water_noise(world * vec2(0.16, 0.37) + vec2(t * 0.34, -t * 0.21));
		crest_gain = smoothstep(0.59, 0.89, fleck) * min(water_params2.x, 2.0) * 0.27;
	}
	float spec = crest * crest_gain
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

// Shallow shores (L1 option, off by default). x: strength 0..1, 0 skips all
// of it so every model draws as before; y: shard daylight for the caustics.

uniform vec2 water_shallow = vec2(0.0, 1.0);

// Shore distance averaged at the cell corners (the same four-cell blend as
// water_wet_linear), so the band fades across tile boundaries. 0 at land, 1 deep.
float water_shore_depth(vec2 xy) {
	vec2 lo = floor(xy);
	vec2 blend = fract(xy);
	return mix(mix(water_mask_at(lo).b, water_mask_at(lo + vec2(1.0, 0.0)).b, blend.x),
			mix(water_mask_at(lo + vec2(0.0, 1.0)).b,
			water_mask_at(lo + vec2(1.0, 1.0)).b, blend.x), blend.y);
}

// One scrolling caustic layer: bright thin ridges of a noise field.
float water_caustic_layer(vec2 p, vec2 dir, float t) {
	float n = water_noise(p + dir * t);
	return pow(1.0 - abs(2.0 * n - 1.0), 5.0);
}

// Small patches of stones, with fish cruising together above the bed.
// All distances here are projected art pixels, independent of tile UV and zoom.
vec3 water_harbor_bed(vec3 bed, vec2 world, float t, float visibility) {
	vec2 patch_size = vec2(118.0, 72.0);
	vec2 patch = floor(world / patch_size);
	float seed = water_hash(mod(patch, 127.0));
	vec2 local = (fract(world / patch_size) - 0.5) * patch_size;
	local -= vec2(seed - 0.5, water_hash(mod(patch.yx, 113.0)) - 0.5) * vec2(24.0, 12.0);
	if (seed > 0.59) {
		for (int i = 0; i < 3; i++) {
			float k = float(i);
			vec2 center = vec2((k - 1.0) * 8.0, sin(seed * 19.0 + k * 2.4) * 4.0);
			vec2 q = (local - center) / (vec2(6.8, 4.2) * (0.7 + 0.24 * k));
			float d = length(q) + 0.10 * sin(q.x * 4.0 + seed);
			float stone = 1.0 - smoothstep(0.79, 1.0, d);
			vec3 slate = mix(vec3(0.08, 0.15, 0.15), vec3(0.34, 0.40, 0.33),
					clamp(0.54 - 0.25 * q.x - 0.44 * q.y, 0.0, 1.0));
			bed = mix(bed, slate, visibility * stone);
		}
	}
	if (seed < 0.18) {
		// Sample once per school position so its silhouettes react together.
		vec2 sample_f;
		vec2 sample_cell = water_cell((patch + 0.5) * patch_size, sample_f);
		float disturbance = clamp(abs(water_ripple(sample_cell + sample_f)) * water_params2.w * 2.5, 0.0, 1.0);
		float phase = t * 0.48 + seed * 35.0;
		vec2 center = vec2(24.0 * sin(phase), 8.0 * cos(phase));
		vec2 dir = normalize(vec2(24.0 * cos(phase), -8.0 * sin(phase)));
		vec2 rel = local - center;
		vec2 fish = vec2(dot(rel, dir), dot(rel, vec2(-dir.y, dir.x)));
		for (int i = 0; i < 3; i++) {
			float k = float(i);
			vec2 q = fish - vec2(-5.0 * k - disturbance * (k + 1.0) * 2.0, (k - 1.0) * (4.0 + disturbance * 5.0) + sin(t * 2.4 + k));
			float body = 1.0 - smoothstep(0.72, 1.0, length(q / vec2(3.6, 1.3)));
			float tail = 1.0 - smoothstep(0.65, 1.0, length((q + vec2(3.9, sin(t * 5.0 + k) * 0.6)) / vec2(1.2, 1.3)));
			bed = mix(bed, vec3(0.045, 0.13, 0.14), max(body, tail) * visibility);
		}
	}
	return bed;
}

// Sandy-teal sea floor showing through near land, with two caustic layers
// drifting in different directions. Generated here; no art.
vec3 water_shallow_apply(vec3 c, vec2 cell, vec2 f, vec2 world, float t) {
	float depth = water_shore_depth(cell + f);
	float band = 1.0 - smoothstep(0.1, 1.0, depth);
	float s = band * water_shallow.x;
	if (s <= 0.0) { return c; }
	float luma = dot(c, vec3(0.299, 0.587, 0.114));
	vec3 floor_c = mix(vec3(0.30, 0.58, 0.52), vec3(0.62, 0.66, 0.46), band * band) * (0.65 + 0.9 * luma);
	if (water_harbor && water_model == WATER_REALISTIC) {
		floor_c = water_harbor_bed(floor_c, world, t, smoothstep(0.05, 0.55, band));
	}
	c = mix(c, floor_c, clamp(s * 0.6, 0.0, 0.6));
	vec2 p = world * 0.06;
	float k = water_caustic_layer(p, vec2(0.11, 0.07), t * 1.4)
			* water_caustic_layer(p * 1.3 + 7.0, vec2(-0.09, 0.12), t * 1.2);
	c += vec3(0.55, 0.85, 0.80) * k * (water_harbor && water_model == WATER_REALISTIC ? 0.80 : 1.6) * s * water_shallow.y;
	return clamp(c, 0.0, 1.0);
}

vec3 water_shade_core(vec3 base, vec2 target_px) {
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
	// These are projected art pixels. Compress only the visible realistic swell;
	// shoreline and disturbance positions below stay in the original cell space.
	if (water_harbor && water_model == WATER_REALISTIC) {
		p.y *= 1.45;
	}
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
	if (water_harbor_swell) {
		vec2 xy = cell + f;
		vec2 slope = 1.7 * vec2(0.26, -0.17) * cos(dot(xy, vec2(0.26, -0.17)) + water_time * 1.26)
				+ 0.9 * vec2(0.13, 0.32) * cos(dot(xy, vec2(0.13, 0.32)) - water_time * 0.87);
		hx += (slope.x - slope.y) / 44.0;
		hy += (slope.x + slope.y) / 44.0;
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

// ONE BLEND A PIXEL
//
// A tile diamond is 44 x 44 art pixels and its neighbours' centres are 22
// away, so neighbouring diamonds share the pixels on the line between them:
// each of a diamond's edge pixels is drawn by two tiles (four at a corner).
// Opaque that is invisible. A translucent surface blends twice there and
// draws a dark line along every tile edge and round every shore-shaped tile.
//
// Those pixels are exactly the ones whose centre lies on a cell boundary,
// which a fragment can tell from its own world position; it cannot tell which
// tile is drawing it, and does not need to: every water tile that covers such
// a pixel takes the alpha that, blended once per wet cell sharing the pixel,
// adds up to the tile's own. Same colour, same result as one blend. Cells
// without water do not count, so the edge against a beach keeps full alpha.
// water_seams 0 leaves the alpha alone: for a data set whose water art was
// trimmed to tile exactly once instead (tools/seabed --seams art).
uniform float water_seams = 1.0;

float water_seam_alpha(float alpha, vec2 target_px) {
	if (alpha >= 0.999 || water_seams < 0.5) { return alpha; }
	float grain = max(water_params3.x, 1.0);
	vec2 world = (floor((target_px * water_target_to_world.xy + water_target_to_world.zw) / grain) + 0.5) * grain;
	vec2 f;
	vec2 cell = water_cell(world, f);
	float z = floor(water_mask_at(cell).g * 255.0 + 0.5) - 128.0;
	// Pixel centres are half-integers, so these sums are whole numbers: a
	// boundary along X where the first is a multiple of 44, along Y the second.
	float a = mod(floor(world.x + world.y + 4.0 * z + 0.5) + 22.0, 44.0);
	float b = mod(floor(world.y + 4.0 * z - world.x + 0.5) + 22.0, 44.0);
	bool on_x = a < 0.5;
	bool on_y = b < 0.5;
	if (!on_x && !on_y) { return alpha; }
	// The cell on the far side of each boundary the pixel lies on; found from
	// a point pushed a quarter cell inside it, so a tie cannot round both ways.
	float u = world.x / 22.0;
	float v = (world.y + 4.0 * z) / 22.0;
	vec2 hi = floor(vec2((u + v) * 0.5, (v - u) * 0.5) + 0.5 + vec2(on_x ? 0.25 : 0.0, on_y ? 0.25 : 0.0));
	float n = water_mask_at(hi).r;
	if (on_x) { n += water_mask_at(hi - vec2(1.0, 0.0)).r; }
	if (on_y) { n += water_mask_at(hi - vec2(0.0, 1.0)).r; }
	if (on_x && on_y) { n += water_mask_at(hi - vec2(1.0, 1.0)).r; }
	n = max(floor(n + 0.5), 1.0);
	return 1.0 - pow(1.0 - alpha, 1.0 / n);
}

// DEPTH FADE
//
// A translucent water tile shows what lies under it: a staged seabed
// (tools/seabed), a shard's own. Where such water ends against the opaque
// deep sea the two met along tile edges, a sawtooth of diamonds. Here the
// surface thickens with the distance from land instead, per pixel: the tile's
// own alpha at the shore, opaque from water_depth_fade.y cells out, where it
// is the same art as the deep sea's land tiles. x is where the thickening
// starts; y <= x switches it off (the tile's alpha everywhere, as before).
uniform vec2 water_depth_fade = vec2(4.0, 12.5);

float water_alpha(float alpha, vec2 target_px) {
	if (alpha >= 0.999 || water_depth_fade.y <= water_depth_fade.x) { return alpha; }
	vec2 world = target_px * water_target_to_world.xy + water_target_to_world.zw;
	vec2 f;
	vec2 cell = water_cell(world, f);
	// The distance is Chebyshev, so its contours are straight along the tile
	// axes; a slow wobble of a cell or so keeps the fade from tracing them.
	float wobble = (water_noise(world * 0.021) - 0.5) * 2.4 + (water_noise(world * 0.067 + 9.0) - 0.5) * 0.9;
	float d = water_far_linear(cell + f) + wobble;
	float k = smoothstep(water_depth_fade.x, water_depth_fade.y, d);
	return mix(alpha, 1.0, k);
}

vec3 water_shade(vec3 base, vec2 target_px) {
	vec3 c = water_shade_core(base, target_px);
	if (water_shallow.x <= 0.0 || water_debug != 0) { return c; }
	float grain = max(water_params3.x, 1.0);
	vec2 world = (floor((target_px * water_target_to_world.xy + water_target_to_world.zw) / grain) + 0.5) * grain;
	vec2 f;
	vec2 cell = water_cell(world, f);
	float t = water_time * water_params.x;
	c = water_shallow_apply(c, cell, f, world, t);
	if (water_harbor && water_model == WATER_REALISTIC && water_mask_post(water_mask_at(cell))) {
		float d = length(f);
		float shadow = 1.0 - smoothstep(0.17, 0.43, d);
		float pulse = sin(t * 1.8 + dot(cell, vec2(0.39, 0.17)));
		float ring = 1.0 - smoothstep(0.012, 0.042, abs(d - (0.36 + 0.035 * pulse)));
		c *= 1.0 - 0.27 * shadow;
		c += vec3(0.11, 0.15, 0.15) * ring * smoothstep(0.40, 0.77, water_noise(world * 0.15 + t * 0.12));
	}
	return c;
}
