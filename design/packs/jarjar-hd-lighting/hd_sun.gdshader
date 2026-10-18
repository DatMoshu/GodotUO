// HD sun directional light (HD light Phase 3): height-buffered sun shadows +
// color-temperature daylight over the world albedo, before hd_tonemap folds
// in the torch light buffer. Runs first in the hd_light look.
//
// How it works: height_tex carries top-Z per pixel (HdHeightBuffer: tile Z,
// tiledata Height, splat bounds; half res desktop / quarter mobile, stamped
// nearest, sampled linear). Each pixel marches toward the sun; the ray
// climbs sun_slope Z per pixel from the receiver's own height, and whatever
// stands above the ray shadows it. The penumbra widens with marched
// distance, so taller casters throw longer, softer shadows and noon (steep
// ray) throws short hard ones. Height samples filter linear (bilinear) so
// buffer texel edges interpolate instead of stair-stepping, and an
// interleaved-gradient-noise dither breaks the 1-Z encode banding across
// the penumbra into film-like grain. The lit albedo takes
// the sun's color x energy; the shadowed falls back to sun_floor (moon-blue
// at night, warm gray by day) so torch light in hd_tonemap still has albedo
// to work with after dark.
//
// sun_strength 0 crossfades the light to exactly 1.0: Phase 1/2 output,
// bit-identical. Stateless, no TIME (auto-cycle advances via uniforms);
// Linear on the height sampler only; the world draw is untouched.
shader_type canvas_item;
render_mode blend_disabled;

uniform sampler2D source : hint_screen_texture, filter_nearest;
uniform sampler2D height_tex : filter_linear;
// Driven per-frame from HdSun (no hints: the menu hides hintless knobs).
uniform vec2 sun_shadow_dir = vec2(-1.0, 0.35);
uniform vec3 sun_color = vec3(1.0);
uniform float sun_energy = 1.0;
uniform vec3 sun_floor = vec3(0.2);
uniform float sun_slope = 0.3;
uniform float sun_px_per_z = 4.0;
uniform int sun_steps = 10;
// Menu knobs (kept, never overwritten per-frame).
uniform float sun_strength : hint_range(0.0, 1.0, 0.01) = 1.0;
uniform float sun_soften : hint_range(0.5, 4.0, 0.1) = 1.5;
uniform float sun_shadow_px : hint_range(1.0, 12.0, 0.5) = 6.0;

float sun_height(vec2 uv) {
	vec2 cuv = clamp(uv, vec2(0.0), vec2(1.0));
	return texture(height_tex, cuv).r * 255.0 - 128.0;
}

void fragment() {
	vec4 c = texture(source, SCREEN_UV);
	float h0 = sun_height(SCREEN_UV);
	// Fixed 12-tap march with a uniform early-out: the governor lowers
	// sun_steps on mobile/zoom without recompiling (dynamic break on a
	// uniform is legal in GLES3; the bound stays constant).
	float shadow = 0.0;
	int steps = clamp(sun_steps, 4, 12);
	float stride_z = sun_shadow_px * 2.0;
	vec2 dir = sun_shadow_dir;
	// Static interleaved gradient noise: breaks the 1-Z encode banding
	// across the penumbra into dithered grain. No TIME (stateless), so the
	// pattern is fixed per pixel; amplitude ~1 Z so fully lit/shadowed
	// pixels never flip, only the ramp. Pixel coords from SCREEN_UV /
	// SCREEN_PIXEL_SIZE (the canvas_item way: no gl_FragCoord here).
	vec2 sun_pix = floor(SCREEN_UV / SCREEN_PIXEL_SIZE);
	float ign = fract(52.9829189 * fract(dot(sun_pix, vec2(0.06711056, 0.00583715))));
	float dith = (ign - 0.5) * (0.6 + 0.4 * sun_soften);
	for (int i = 1; i <= 12; i++) {
		if (i > steps) { break; }
		float dist_z = stride_z * float(i);
		float dist_px = dist_z * sun_px_per_z;
		// March TOWARD the sun: opposite the shadow-fall vector (shadows
		// fall away from the sun, casters stand between the pixel and it).
		vec2 uv = SCREEN_UV - dir * dist_px * SCREEN_PIXEL_SIZE;
		float hi = sun_height(uv);
		float ray = h0 + dist_z * sun_slope;
		// Penumbra widens with distance: near contact stays crisp, long
		// dawn/dusk throws go soft. Base + slope widened ~1.5x over the
		// blocky-era 0.75 + dist * 0.12: the old near-tap width (~2.9 at
		// defaults) quantized wall steps into visible stairs; the new
		// near width (~4.5) plus linear sampling + dither reads as a
		// smooth gradient. Still scales with sun_soften and distance, so
		// the menu knob and the dawn/dusk softness curve behave as before.
		float w = 1.25 + dist_z * 0.18 * sun_soften;
		shadow = max(shadow, clamp((hi - ray + dith) / w, 0.0, 1.0));
	}
	float lit = clamp(1.0 - shadow, 0.0, 1.0);
	vec3 light = mix(sun_floor, sun_color * sun_energy, lit);
	// Strength crossfades to exactly 1.0: 0 is Phase 1/2, bit-identical.
	light = mix(vec3(1.0), light, sun_strength);
	// Night moon floor: the sun x blob double keeps midnight near 0.03, and
	// ACES crushes that to black (world gone, torch blobs floating). Floor
	// the moon to dimly-visible so torches pool over albedo instead of void.
	// Below every day value, so day is bit-identical; strength 0 stays 1.0.
	light = max(light, vec3(0.20, 0.22, 0.30) * sun_strength + vec3(1.0) * (1.0 - sun_strength));
	COLOR = vec4(c.rgb * light, c.a);
}
