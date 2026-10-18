// Rain: falling streaks over the world, driven by weather intensity (ADR-0023).
shader_type canvas_item;
render_mode blend_disabled;

uniform sampler2D source : hint_screen_texture, filter_nearest;
uniform float intensity : hint_range(0.0, 1.0, 0.01) = 0.0;
uniform float speed : hint_range(0.0, 4.0, 0.05) = 2.0;

float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }

void fragment() {
	vec4 c = texture(source, SCREEN_UV);
	float r = 0.0;
	for (int layer = 0; layer < 3; layer++) {
		float ls = 1.0 + float(layer) * 0.5;
		vec2 ruv = SCREEN_UV * vec2(100.0 * ls, 40.0 * ls);
		ruv.y += TIME * speed * (1.0 + float(layer) * 0.3) * 10.0;
		vec2 cell = floor(ruv);
		float dr = hash(cell);
		if (dr > 1.0 - intensity * 0.3) {
			vec2 cuv = fract(ruv);
			float dx = hash(cell + vec2(0.5, 0.0));
			float dy = fract(dr * 10.0 + TIME * speed * (1.0 + float(layer) * 0.3));
			float ax = abs(cuv.x - dx);
			if (ax < 0.02 && cuv.y > dy && cuv.y < dy + 0.08) {
				r += 1.0 / float(layer + 1);
			}
		}
	}
	vec3 tint = vec3(0.7, 0.8, 0.9);
	vec3 outc = mix(c.rgb, tint, clamp(r * intensity, 0.0, 1.0));
	outc *= 1.0 - intensity * 0.2;
	COLOR = vec4(outc, c.a);
}
