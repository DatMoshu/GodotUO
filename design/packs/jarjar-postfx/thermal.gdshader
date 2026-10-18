// Thermal vision: luminance mapped through a black-blue-green-yellow-red-white ramp (ADR-0023).
shader_type canvas_item;
render_mode blend_disabled;

uniform sampler2D source : hint_screen_texture, filter_nearest;
uniform float intensity : hint_range(0.0, 1.0, 0.01) = 1.0;

void fragment() {
	vec4 c = texture(source, SCREEN_UV);
	float h = dot(c.rgb, vec3(0.299, 0.587, 0.114));
	vec3 t;
	if (h < 0.2) {
		t = mix(vec3(0.0), vec3(0.0, 0.0, 0.5), h / 0.2);
	} else if (h < 0.4) {
		t = mix(vec3(0.0, 0.0, 0.5), vec3(0.0, 0.5, 0.0), (h - 0.2) / 0.2);
	} else if (h < 0.6) {
		t = mix(vec3(0.0, 0.5, 0.0), vec3(1.0, 1.0, 0.0), (h - 0.4) / 0.2);
	} else if (h < 0.8) {
		t = mix(vec3(1.0, 1.0, 0.0), vec3(1.0, 0.0, 0.0), (h - 0.6) / 0.2);
	} else {
		t = mix(vec3(1.0, 0.0, 0.0), vec3(1.0), (h - 0.8) / 0.2);
	}
	COLOR = vec4(mix(c.rgb, t, intensity), c.a);
}
