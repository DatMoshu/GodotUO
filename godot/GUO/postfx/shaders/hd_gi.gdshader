// HD torch GI, Phase 4 (ADR-0023): coarse-but-true 2D radiance cascades.
//
// What it replaces: the blob multiply (world x torch-light buffer) that
// hd_tonemap does in Phases 1-3. With an enabled hd_gi pass at gi_strength
// above zero the PostFx stack tells hd_tonemap to skip its multiply
// (hd_skip_light, pushed per-frame like the sun uniforms), and THIS pass
// composites instead: true inverse-square falloff + soft height-buffered
// shadows + one-bounce color bleeding + floor/wall bounce. gi_strength 0 is
// a pure passthrough, so strength 0 (or the pass disabled) is bit-identical
// to Phase 3.
//
// Buffers (deliberately virtual — no new viewport, no CPU fill, stateless):
// - EMISSIVE: the torch light buffer (light_tex) gated by gi_threshold, so
//   only true hotspots radiate (torches, fire, spells, lava light, sconces).
//   That buffer IS the _lights offers rasterized (color x HDR energy x
//   flicker), so this reuses the offers + light colors with zero extra work.
//   Plus a small albedo-hotspot term: bright warm albedo (lava pools, lit
//   windows, the flame pixels themselves) emits a little, so things glow
//   even where no light offer stands.
// - ALBEDO: the source itself (the hd_sun output) sampled at the probe taps.
//   Probe spacing on the far ring is ~4 px, i.e. quarter-res density, which
//   is the "world albedo downsampled, quarter-res is fine" requirement met
//   by sampling instead of by a second viewport.
// - OCCLUSION: the real HdHeightBuffer (height_tex, top-Z per pixel).
//
// Cascades (3 rings x 8 rays, probe-snapped, fixed loops with a uniform
// ring gate so the governor never recompiles):
//   ring 0:  8 rays at  14 px, 1 height tap each  (contact + crisp cores)
//   ring 1:  8 rays at  56 px, 2 height taps each (mid + end, soft walls)
//   ring 2:  8 rays at 130 px, 2 height taps each, half-step rotated,
//            snapped to a 4 px probe grid (coarse far field)
// Desktop (gi_steps 10): 24 taps x 4 samples = ~100 taps/px. Mobile or
// extreme zoom (gi_steps 6): rings 0-1 only, ~60. Below 6: ring 0 only.
// The far range scales with gi_range (260 px default ~= 6 tiles).
//
// Per tap: emissive x albedo-tint (the bleed: torch orange x red wall =
// red-orange bounce; gi_bleed 0 keeps the raw light color), inverse-square
// falloff 1/(1+(d/gi_falloff)^2) (TRUE falloff, gi_falloff 42 px default),
// 2.5D transmittance (a march point blocks when its top stands above both
// ends + 1 Z, softened by gi_soften), and a bilateral-ish height-similarity
// weight so the coarse field does not leak through walls. Taps accumulate a
// MEAN (not a sum), so governor ring changes barely move the brightness,
// then scale by gi_bounce x 1.5 and tint the receiver albedo. Stateless: no
// TIME, no inter-frame feedback (ADR-0023 rule); torch flicker arrives via
// the light buffer's CPU-side energy hash.
//
// Quality/perf tradeoffs (measured basis in the report):
// - 8 rays/ring is coarse: thin poles may be missed between rays; the blob
//   direct term keeps every hotspot itself crisp, only the bounce is coarse.
// - 1 mid-tap occlusion is binary-ish up close; gi_soften widens it.
// - Mean-normalization bounds the energy (no blowout) but underplays tight
//   clusters of torches vs a true sum; gi_bounce compensates.
// - Mobile halves the whole stack resolution (// postfx: heavy below) AND
//   drops a ring: expect ~1/4 the desktop cost, chunkier bounce.
// Nearest throughout; the world draw is untouched.
shader_type canvas_item;
render_mode blend_disabled;
// postfx: heavy (up to 24 probe taps x 4 samples a pixel; half resolution on the mobile tier, ADR-0023)

uniform sampler2D source : hint_screen_texture, filter_nearest;
uniform sampler2D light_tex : filter_nearest;
uniform sampler2D height_tex : filter_linear;

uniform float gi_strength : hint_range(0.0, 1.0, 0.01) = 1.0;
uniform float gi_range : hint_range(80.0, 400.0, 5.0) = 260.0;
uniform float gi_bleed : hint_range(0.0, 1.0, 0.01) = 0.6;
uniform float gi_bounce : hint_range(0.0, 1.0, 0.01) = 0.5;
uniform float gi_soften : hint_range(0.5, 4.0, 0.1) = 1.5;
uniform float gi_threshold : hint_range(0.0, 1.0, 0.01) = 0.32;
uniform float gi_falloff : hint_range(10.0, 120.0, 1.0) = 42.0;
// Governor (no hint: the menu hides hintless knobs): 10 desktop (3 rings),
// 6 mobile/extreme zoom (rings 0-1). Pushed per-frame from HdGi.Steps.
uniform int gi_steps = 10;
// Phase 5 weather (pushed per-frame from HdWeather; no hint so the menu
// hides it): wet surfaces bounce harder. 0 is Phase 4, bit-identical.
uniform float gi_wet = 0.0;

float gi_height(vec2 uv) {
	vec2 cuv = clamp(uv, vec2(0.0), vec2(1.0));
	return texture(height_tex, cuv).r * 255.0 - 128.0;
}

vec3 gi_light(vec2 uv) {
	vec2 cuv = clamp(uv, vec2(0.0), vec2(1.0));
	return texture(light_tex, cuv).rgb;
}

vec3 gi_albedo(vec2 uv) {
	vec2 cuv = clamp(uv, vec2(0.0), vec2(1.0));
	return texture(source, cuv).rgb;
}

// 2.5D visibility between receiver (height h0) and tap (height h1): the
// midpoint blocks when its top stands above both ends + 1 Z (a wall between
// two floor pixels), softened by pen (Z units). The emitter height is
// unknown (approximation, documented above): both ends are assumed near the
// surfaces that radiate/receive, which holds for torches on walls/floors.
float gi_transmit(float h0, float h1, vec2 p, vec2 q, float pen) {
	float hm = gi_height((p + q) * 0.5);
	float top = max(h0, h1) + 1.0;
	return clamp(1.0 - (hm - top) / pen, 0.0, 1.0);
}

void fragment() {
	vec4 c = texture(source, SCREEN_UV);
	vec4 lt0 = texture(light_tex, SCREEN_UV);
	// Same guard as hd_tonemap: no light override (classic cleared target)
	// means no darkening.
	vec3 blob = lt0.a < 0.01 ? vec3(1.0) : lt0.rgb;
	if (gi_strength > 0.001) {
		float h0 = gi_height(SCREEN_UV);
		float rs = gi_range / 260.0;
		int rings = gi_steps >= 10 ? 3 : (gi_steps >= 6 ? 2 : 1);
		float pen = max(gi_soften, 0.25);
		vec2 px = SCREEN_PIXEL_SIZE;
		vec3 acc = vec3(0.0);
		float wsum = 0.0;
		// Ring 0: contact. No snap (texel grid is the probe grid this close).
		for (int k = 0; k < 8; k++) {
			float a = float(k) * 0.7853982;
			float d = 14.0 * rs;
			vec2 q = clamp(SCREEN_UV + vec2(cos(a), sin(a)) * d * px, vec2(0.0), vec2(1.0));
			vec3 lt = gi_light(q);
			vec3 al = gi_albedo(q);
			float h1 = gi_height(q);
			float lum = max(max(lt.r, lt.g), lt.b);
			float em = smoothstep(gi_threshold, gi_threshold + 0.3, lum);
			float al_lum = dot(al, vec3(0.299, 0.587, 0.114));
			float warm = clamp((al.r - max(al.g, al.b)) * 2.0, 0.0, 1.0);
			float am = smoothstep(0.55, 0.9, al_lum) * (0.3 + 0.7 * warm);
			vec3 rad = lt * em + al * (am * 0.35);
			vec3 tinted = rad * mix(vec3(1.0), al, gi_bleed);
			float w = 1.0 / (1.0 + pow(d / gi_falloff, 2.0));
			float tw = w * gi_transmit(h0, h1, SCREEN_UV, q, pen)
				* clamp(1.0 - abs(h1 - h0) / (8.0 * pen), 0.2, 1.0);
			acc += tinted * tw;
			wsum += tw;
		}
		// Ring 1: walls and across-the-room. Snapped to a 2 px probe grid.
		if (rings >= 2) {
			for (int k = 0; k < 8; k++) {
				float a = float(k) * 0.7853982;
				float d = 56.0 * rs;
				vec2 q = clamp(SCREEN_UV + vec2(cos(a), sin(a)) * d * px, vec2(0.0), vec2(1.0));
				q = clamp((floor(q / (2.0 * px)) + 0.5) * (2.0 * px), vec2(0.0), vec2(1.0));
				vec3 lt = gi_light(q);
				vec3 al = gi_albedo(q);
				float h1 = gi_height(q);
				float lum = max(max(lt.r, lt.g), lt.b);
				float em = smoothstep(gi_threshold, gi_threshold + 0.3, lum);
				float al_lum = dot(al, vec3(0.299, 0.587, 0.114));
				float warm = clamp((al.r - max(al.g, al.b)) * 2.0, 0.0, 1.0);
				float am = smoothstep(0.55, 0.9, al_lum) * (0.3 + 0.7 * warm);
				vec3 rad = lt * em + al * (am * 0.35);
				vec3 tinted = rad * mix(vec3(1.0), al, gi_bleed);
				float w = 1.0 / (1.0 + pow(d / gi_falloff, 2.0));
				float tw = w * gi_transmit(h0, h1, SCREEN_UV, q, pen)
					* clamp(1.0 - abs(h1 - h0) / (8.0 * pen), 0.2, 1.0);
				acc += tinted * tw;
				wsum += tw;
			}
		}
		// Ring 2: far field. Half-step rotated, snapped to a 4 px grid
		// (quarter-res probe density, as specified).
		if (rings >= 3) {
			for (int k = 0; k < 8; k++) {
				float a = float(k) * 0.7853982 + 0.3926991;
				float d = 130.0 * rs;
				vec2 q = clamp(SCREEN_UV + vec2(cos(a), sin(a)) * d * px, vec2(0.0), vec2(1.0));
				q = clamp((floor(q / (4.0 * px)) + 0.5) * (4.0 * px), vec2(0.0), vec2(1.0));
				vec3 lt = gi_light(q);
				vec3 al = gi_albedo(q);
				float h1 = gi_height(q);
				float lum = max(max(lt.r, lt.g), lt.b);
				float em = smoothstep(gi_threshold, gi_threshold + 0.3, lum);
				float al_lum = dot(al, vec3(0.299, 0.587, 0.114));
				float warm = clamp((al.r - max(al.g, al.b)) * 2.0, 0.0, 1.0);
				float am = smoothstep(0.55, 0.9, al_lum) * (0.3 + 0.7 * warm);
				vec3 rad = lt * em + al * (am * 0.35);
				vec3 tinted = rad * mix(vec3(1.0), al, gi_bleed);
				float w = 1.0 / (1.0 + pow(d / gi_falloff, 2.0));
				float tw = w * gi_transmit(h0, h1, SCREEN_UV, q, pen)
					* clamp(1.0 - abs(h1 - h0) / (8.0 * pen), 0.2, 1.0);
				acc += tinted * tw;
				wsum += tw;
			}
		}
		vec3 avg = acc / max(wsum, 1e-4);
		vec3 indirect = c.rgb * avg * (gi_bounce * 1.5) * (1.0 + clamp(gi_wet, 0.0, 1.0) * 0.5);
		vec3 light = blob + indirect * gi_strength;
		COLOR = vec4(c.rgb * light, c.a);
	} else {
		// Strength 0: passthrough. hd_tonemap keeps its blob multiply
		// (the stack only sets hd_skip_light while a pass is active above
		// zero), so this is Phase 3, bit-identical.
		COLOR = c;
	}
}
