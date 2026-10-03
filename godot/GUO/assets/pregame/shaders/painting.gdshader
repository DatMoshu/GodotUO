// The pregame's backdrop: the client's own login painting, read at runtime
// from the player's files, drawn over the whole window. GUO, BSD-2-Clause.
// - xBR upscaling (xbr.gdshaderinc), fitted to the window's height
// - wider windows: the painting's outer columns mirrored outward and
//   darkened toward the screen edge, so there are no bars and nothing cut
// - warm flickering light pools, a slow drift, a vignette, and a dim for the
//   steps that put panels over it
// Target: canvas_item, any renderer. One full-screen pass.
shader_type canvas_item;

#include "res://assets/pregame/shaders/xbr.gdshaderinc"

uniform sampler2D painting : filter_nearest, repeat_disable;
uniform vec2 painting_size = vec2(640.0, 480.0);
// The window in pixels; where the painting's top-left lands and its scale.
uniform vec2 screen_size = vec2(1280.0, 800.0);
uniform vec2 origin = vec2(0.0);
uniform float scale = 1.6667;
uniform vec2 drift = vec2(0.0);
uniform float dim = 1.0;
uniform float time_s = 0.0;
// Up to 4 light pools: xy = centre in painting pixels, z = radius, w = strength.
uniform vec4 lights[4];
uniform vec3 light_color : source_color = vec3(1.0, 0.72, 0.42);
uniform float vignette = 0.45;

float flicker(float seed) {
	float t = time_s * 7.0 + seed * 13.0;
	return 0.82 + 0.1 * sin(t) + 0.06 * sin(t * 2.3 + 1.7) + 0.04 * sin(t * 5.1 + seed);
}

void fragment() {
	vec2 screen = UV * screen_size;
	vec2 p = (screen - origin - drift) / scale;
	float outside = 0.0;

	// Mirror the outer columns (and rows) outward.
	if (p.x < 0.0) { outside = -p.x; p.x = min(-p.x, painting_size.x - 1.0); }
	if (p.x > painting_size.x) { outside = p.x - painting_size.x; p.x = max(2.0 * painting_size.x - p.x, 0.0); }
	if (p.y < 0.0) { outside = max(outside, -p.y); p.y = min(-p.y, painting_size.y - 1.0); }
	if (p.y > painting_size.y) { outside = max(outside, p.y - painting_size.y); p.y = max(2.0 * painting_size.y - p.y, 0.0); }

	vec4 c = xbr_sample(painting, p / painting_size, 1.0 / painting_size);

	// The mirrored wall fades toward the screen edge.
	c.rgb *= mix(1.0, 0.25, smoothstep(0.0, painting_size.x * 0.45, outside));

	vec3 light = vec3(0.0);
	for (int i = 0; i < 4; i++) {
		vec4 l = lights[i];
		if (l.z > 0.0) {
			float f = 1.0 - smoothstep(0.0, l.z, distance(p, l.xy));
			light += light_color * f * f * l.w * flicker(float(i));
		}
	}
	c.rgb *= 0.82 + light;

	vec2 v = UV - 0.5;
	c.rgb *= 1.0 - vignette * smoothstep(0.25, 0.75, length(v * vec2(screen_size.x / screen_size.y, 1.0)) * 0.9);
	COLOR = vec4(c.rgb * dim, 1.0);
}
