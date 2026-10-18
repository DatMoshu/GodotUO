// Snow: drifting flakes over the world, driven by weather intensity (ADR-0023).
shader_type canvas_item;
render_mode blend_disabled;

uniform sampler2D source : hint_screen_texture, filter_nearest;
uniform float intensity : hint_range(0.0, 1.0, 0.01) = 0.0;
uniform float speed : hint_range(0.0, 2.0, 0.01) = 0.5;

float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }

void fragment() {
	vec4 c = texture(source, SCREEN_UV);
	float s = 0.0;
	for (int layer = 0; layer < 4; layer++) {
		float ls = 20.0 + float(layer) * 15.0;
		vec2 suv = SCREEN_UV * ls;
		suv.y += TIME * speed * (0.5 + float(layer) * 0.2);
		suv.x += sin(TIME * 0.5 + float(layer)) * 0.3;
		vec2 cell = floor(suv);
		float fr = hash(cell);
		if (fr > 1.0 - intensity * 0.5) {
			vec2 cuv = fract(suv);
			vec2 fp = vec2(hash(cell + vec2(0.1, 0.0)), hash(cell + vec2(0.0, 0.2)));
			float d = length(cuv - fp);
			float fs = 0.01 * (0.5 + hash(cell + vec2(0.3, 0.0)) * 0.5);
			if (d < fs) {
				s += (1.0 - d / fs) * (1.0 / float(layer + 1));
			}
		}
	}
	vec3 outc = mix(c.rgb, vec3(1.0), clamp(s * intensity, 0.0, 1.0));
	outc += intensity * 0.05;
	COLOR = vec4(clamp(outc, 0.0, 1.0), c.a);
}
