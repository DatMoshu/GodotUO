// GUO procedural rain, ADR-0048. One canvas pass, three data-only sheet regions.
shader_type canvas_item;
render_mode unshaded;

uniform sampler2D surface_buffer : filter_nearest, repeat_disable;
uniform sampler2D drop_sheet : filter_nearest, repeat_disable;
uniform vec2 target_size = vec2(1280.0, 720.0);
// Integer camera-centre tile, kept at float32 precision outside RGBA16F.
uniform vec2 surface_origin = vec2(0.0);
uniform float seconds = 0.0;
uniform float intensity : hint_range(0.0, 1.0) = 0.0;
uniform float wind = 0.0;
// Probe isolation: bits 1 streaks, 2 splashes, 4 drips. Production uses all.
uniform int component_mask = 7;

float hash(vec2 p) {
	return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}

// Each region is 32x32: streak, splash ring, roof-edge drip.
float sheet(vec2 uv, float region) {
	return texture(drop_sheet, vec2((clamp(uv.x, 0.0, 1.0) + region) / 3.0,
		clamp(uv.y, 0.0, 1.0))).r;
}

void fragment() {
	vec2 uv = SCREEN_UV;
	vec4 surface = texture(surface_buffer, uv);
	int packed = int(round(surface.w));
	int kind = packed & 15;
	bool covered = ((packed / 16) & 1) != 0;
	float alpha = 0.0;
	if (!covered && intensity > 0.0) {
		vec2 pixel = uv * target_size;
		// Fixed real-time velocity. The grid is screen-space precipitation;
		// density and sprite phase remain independent of frame cadence.
		vec2 falling = pixel - vec2((-110.0 + wind * 100.0) * seconds, 600.0 * seconds);
		falling.x -= falling.y * (-0.18 + wind * 0.16);
		vec2 cells = falling / vec2(38.0, 82.0);
		vec2 tile = floor(cells);
		vec2 local = fract(cells);
		vec2 centre = vec2(0.25 + hash(tile) * 0.5, 0.2 + hash(tile + 71.0) * 0.6);
		vec2 drop_uv = (local - centre) * vec2(9.0, 3.0) + 0.5;
		if ((component_mask & 1) != 0 && hash(tile + 5.0) < intensity && all(greaterThanEqual(drop_uv, vec2(0.0)))
			&& all(lessThanEqual(drop_uv, vec2(1.0)))) {
			alpha = sheet(drop_uv, 0.0) * 0.45;
		}
		if (kind == 1 && (component_mask & 2) != 0) {
			// Splash centres live in world tiles so camera movement cannot slide
			// impact positions across the ground. Final cover/class mask is per pixel.
			// ADR-0048: restore absolute XY before any world-space lookup.
			vec2 world_cells = (surface.xy + surface_origin) * 2.0;
			vec2 cell = floor(world_cells);
			float phase = fract(seconds * 2.0 + hash(cell));
			vec2 splash_uv = (fract(world_cells) - 0.5) / max(phase, 0.05) + 0.5;
			if (hash(cell + 23.0) < intensity && all(greaterThanEqual(splash_uv, vec2(0.0)))
				&& all(lessThanEqual(splash_uv, vec2(1.0)))) {
				alpha = max(alpha, sheet(splash_uv, 1.0) * (1.0 - phase) * 0.5);
			}
		}
		// Runoff begins immediately below a visible roof edge. The current
		// pixel must still be exposed; a covered porch never receives drips.
		if ((component_mask & 4) != 0 && uv.y > 8.0 / target_size.y) {
			int above = int(round(texture(surface_buffer, uv - vec2(0.0, 8.0 / target_size.y)).w)) & 15;
			if (above == 5 && kind != 5) {
				float phase = fract(pixel.y / 8.0 - seconds * 3.0 + hash(vec2(floor(pixel.x / 8.0), 0.0)));
				alpha = max(alpha, sheet(vec2(fract(pixel.x / 8.0), phase), 2.0) * intensity * 0.7);
			}
		}
	}
	COLOR = vec4(0.68, 0.78, 0.88, alpha);
}
