// HD tonemap composite (ADR-0023, HD light Phase 1 + Phase 2 normals):
// world * light in linear-ish + ACES approx + grade.
//
// Phase 2: UO art ships no normal maps, so the normal is a cheap screen-space
// luma-gradient Sobel (the same 3x3 as normal_light.gdshader) on the world,
// and the light direction is the screen-space gradient of the light buffer
// (uphill = toward the nearest torch). N dot L modulates the light — a
// diffuse falloff — and warms facing pixels toward the light's own hue, so
// wall faces toward a flame warm up and backsides fall off. World and lights
// live in separate viewports (the batcher path), so Godot's PointLight2D
// normal-map path cannot apply: this composite term IS the per-light
// color/energy response (the buffer already carries each PointLight2D's color
// x HDR energy x flicker). hd_normal_strength 0 is bit-identical to Phase 1.
// Nearest throughout; no world-shader change.
shader_type canvas_item;
render_mode blend_disabled;

uniform sampler2D source : hint_screen_texture, filter_nearest;
uniform sampler2D light_tex : filter_nearest;
uniform float exposure : hint_range(-2.0, 2.0, 0.01) = 0.0;
uniform float contrast : hint_range(0.5, 2.0, 0.01) = 1.0;
uniform float saturation : hint_range(0.0, 2.0, 0.01) = 1.0;
uniform float temperature : hint_range(-1.0, 1.0, 0.01) = 0.0;
uniform float tint : hint_range(-1.0, 1.0, 0.01) = 0.0;
uniform float lift : hint_range(-0.2, 0.2, 0.005) = 0.0;
uniform float gain : hint_range(0.5, 1.5, 0.01) = 1.0;
// Phase 2 normals: 0 disables the term (Phase 1 output, exactly).
uniform float hd_normal_strength : hint_range(0.0, 1.0, 0.01) = 0.65;
uniform float hd_normal_depth : hint_range(1.0, 10.0, 0.1) = 2.0;
uniform float hd_warmth : hint_range(0.0, 1.0, 0.01) = 0.35;
uniform float hd_light_height : hint_range(0.1, 2.0, 0.05) = 0.6;
// Phase 4 GI: 1 while an enabled hd_gi pass composites above zero (pushed
// per-frame by the PostFx stack, like the sun uniforms; no hint so the menu
// hides it). The torch light is already folded into source, so the multiply
// AND the gradient normals term are skipped; 0 is Phase 1/2/3, bit-identical.
uniform float hd_skip_light = 0.0;
// Phase 5 weather (pushed per-frame from HdWeather; no hints so the menu
// hides them): hd_wet darkens albedo slightly and reflects torchlight on
// damp ground; hd_flash adds the lightning strike (cool ambient + albedo
// term, wired to thunder timing). 0/0 is Phase 4, bit-identical.
uniform float hd_wet = 0.0;
uniform float hd_flash = 0.0;

// Verified on pinned 4.7.2: Environment tonemap/glow are 3D-only, unavailable
// to canvas_item, so this is an ACES approx (Narkowicz) in-shader.
vec3 aces(vec3 x) {
	return clamp((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14), vec3(0.0), vec3(1.0));
}

float hd_world_luma(vec2 uv) {
	vec2 cuv = clamp(uv, vec2(0.0), vec2(1.0));
	return dot(texture(source, cuv).rgb, vec3(0.299, 0.587, 0.114));
}

float hd_light_luma(vec2 uv) {
	vec2 cuv = clamp(uv, vec2(0.0), vec2(1.0));
	vec3 lt = texture(light_tex, cuv).rgb;
	return max(max(lt.r, lt.g), lt.b);
}

void fragment() {
	vec4 c = texture(source, SCREEN_UV);
	vec4 lt = texture(light_tex, SCREEN_UV);
	// No lights (classic target cleared transparent) means no darkening.
	// Phase 4: hd_gi already composited (hd_skip_light) — multiply by 1.
	vec3 mult = hd_skip_light > 0.5 ? vec3(1.0) : (lt.a < 0.01 ? vec3(1.0) : lt.rgb);
	// Linear-ish composite: the HDR light viewport carries >1 hotspots when
	// the HD toggle enabled UseHdr2D; LDR realistic lights stay <=1 here.
	vec3 linear = c.rgb * mult * exp2(exposure);
	// Phase 2 normals (additive handling: the LIGHT is modulated, the albedo
	// is untouched — no world-shader redesign; uo_hue_core's luma weights and
	// normalize-then-max(dot, 0) shape are followed). Skipped entirely when
	// the strength is 0 (Phase 1, bit-identical), where there is no light, or
	// when Phase 4 GI already composited (its bounce carries the facing term).
	if (hd_normal_strength > 0.001 && lt.a >= 0.01 && hd_skip_light < 0.5) {
		float world_luma = dot(c.rgb, vec3(0.299, 0.587, 0.114));
		// Same gate as normal_light.gdshader: flat/dark areas keep ambient.
		float world_mask = smoothstep(0.05, 0.25, world_luma);
		if (world_mask > 0.001) {
			vec2 px = SCREEN_PIXEL_SIZE;
			// World normal: luma Sobel, as in normal_light.gdshader.
			float tl = hd_world_luma(SCREEN_UV - vec2(px.x, px.y));
			float tc = hd_world_luma(SCREEN_UV - vec2(0.0, px.y));
			float tr = hd_world_luma(SCREEN_UV + vec2(px.x, -px.y));
			float ml = hd_world_luma(SCREEN_UV - vec2(px.x, 0.0));
			float mr = hd_world_luma(SCREEN_UV + vec2(px.x, 0.0));
			float bl = hd_world_luma(SCREEN_UV + vec2(-px.x, px.y));
			float bc = hd_world_luma(SCREEN_UV + vec2(0.0, px.y));
			float br = hd_world_luma(SCREEN_UV + vec2(px.x, px.y));
			float dx = (tr + 2.0 * mr + br) - (tl + 2.0 * ml + bl);
			float dy = (bl + 2.0 * bc + br) - (tl + 2.0 * tc + tr);
			vec3 n = normalize(vec3(-dx * hd_normal_depth, -dy * hd_normal_depth, 1.0));
			// Light direction: central differences of the light buffer;
			// the gradient points uphill, toward the nearest torch.
			float lxp = hd_light_luma(SCREEN_UV + vec2(px.x, 0.0));
			float lxm = hd_light_luma(SCREEN_UV - vec2(px.x, 0.0));
			float lyp = hd_light_luma(SCREEN_UV + vec2(0.0, px.y));
			float lym = hd_light_luma(SCREEN_UV - vec2(0.0, px.y));
			vec2 grad = vec2(lxp - lxm, lyp - lym);
			float gmag = length(grad);
			float lc = hd_light_luma(SCREEN_UV);
			// Only near a real falloff (not flat ambient/daylight) and only
			// where the light is meaningfully above the night ambient.
			float gmask = smoothstep(0.004, 0.03, gmag) * smoothstep(0.3, 0.7, lc);
			float m = hd_normal_strength * gmask * world_mask;
			if (m > 0.001) {
				vec2 to_light = grad / max(gmag, 1e-4);
				vec3 L = normalize(vec3(to_light, hd_light_height));
				float diff = max(dot(n, L), 0.0);
				// Diffuse falloff about 1.0: facing ~1.4x, backing ~0.6x.
				float nd = 0.6 + 0.8 * diff;
				linear *= mix(1.0, nd, m);
				// Warm-up: facing pixels take the light's own hue (a torch
				// is orange in the buffer, so walls facing it warm up).
				float ll = max(max(lt.rgb.r, lt.rgb.g), lt.rgb.b);
				vec3 ltint = lt.rgb / max(ll, 1e-3);
				linear *= mix(vec3(1.0), clamp(ltint, vec3(0.0), vec3(1.5)), m * diff * hd_warmth);
			}
		}
	}
	// Phase 5 weather in linear, before the tonemap: wet ground reads darker
	// and throws torchlight back; lightning is a cool strike over everything.
	// Guarded to nothing at 0/0 (Phase 4, bit-identical).
	if (hd_wet > 0.001 || hd_flash > 0.001) {
		float wet = clamp(hd_wet, 0.0, 1.0);
		float flash = clamp(hd_flash, 0.0, 1.0);
		// Damp albedo: slightly darker, like rain-soaked dirt.
		linear *= (1.0 - 0.14 * wet);
		// Wet specular: where real torchlight falls, the ground reflects a
		// share of it back tinted by the albedo (puddle-less sheen, no new
		// buffer; the light buffer already carries color x energy x flicker).
		if (wet > 0.001 && lt.a >= 0.01) {
			float wl = max(max(lt.rgb.r, lt.rgb.g), lt.rgb.b);
			float spec = smoothstep(0.25, 0.85, wl) * wet;
			if (spec > 0.001) {
				linear += c.rgb * lt.rgb * (spec * 0.45);
			}
		}
		// Lightning: cool ambient lift plus an albedo-scaled strike, so white
		// walls flash harder than dark dirt, like the viewport ambient does.
		if (flash > 0.001) {
			linear += (c.rgb * vec3(0.85, 0.90, 1.0) * 0.85 + vec3(0.10, 0.11, 0.14)) * flash;
		}
	}
	vec3 mapped = aces(linear);
	// Cheap grade after the tonemap (same controls as grade.gdshader).
	mapped += vec3(temperature * 0.08, tint * -0.06, -temperature * 0.08);
	mapped = (mapped - 0.5) * contrast + 0.5;
	float l = dot(mapped, vec3(0.299, 0.587, 0.114));
	mapped = mix(vec3(l), mapped, saturation);
	mapped = mapped * gain + lift * (1.0 - mapped);
	COLOR = vec4(clamp(mapped, vec3(0.0), vec3(1.0)), c.a);
}
