// Frosted glass with noise-mixed desaturation and a slight blur (ADR-0023).
shader_type canvas_item;
render_mode blend_disabled;

uniform sampler2D source : hint_screen_texture, filter_nearest;
uniform float intensity : hint_range(0.0, 1.0, 0.01) = 0.5;

void fragment() {
	vec2 uv = SCREEN_UV;
	vec2 p1 = uv * 10.0 + vec2(TIME * 0.03, 0.0);
	vec2 i1 = floor(p1);
	vec2 f1 = fract(p1);
	vec2 u1 = f1 * f1 * (3.0 - 2.0 * f1);
	float a1 = fract(sin(dot(i1, vec2(12.9898, 78.233))) * 43758.5453);
	float b1 = fract(sin(dot(i1 + vec2(1.0, 0.0), vec2(12.9898, 78.233))) * 43758.5453);
	float c1 = fract(sin(dot(i1 + vec2(0.0, 1.0), vec2(12.9898, 78.233))) * 43758.5453);
	float d1 = fract(sin(dot(i1 + vec2(1.0, 1.0), vec2(12.9898, 78.233))) * 43758.5453);
	float n1 = mix(mix(a1, b1, u1.x), mix(c1, d1, u1.x), u1.y);
	vec2 p2 = uv * 10.0 + vec2(37.0, 17.0 - TIME * 0.02);
	vec2 i2 = floor(p2);
	vec2 f2 = fract(p2);
	vec2 u2 = f2 * f2 * (3.0 - 2.0 * f2);
	float a2 = fract(sin(dot(i2, vec2(12.9898, 78.233))) * 43758.5453);
	float b2 = fract(sin(dot(i2 + vec2(1.0, 0.0), vec2(12.9898, 78.233))) * 43758.5453);
	float c2 = fract(sin(dot(i2 + vec2(0.0, 1.0), vec2(12.9898, 78.233))) * 43758.5453);
	float d2 = fract(sin(dot(i2 + vec2(1.0, 1.0), vec2(12.9898, 78.233))) * 43758.5453);
	float n2 = mix(mix(a2, b2, u2.x), mix(c2, d2, u2.x), u2.y);
	vec2 duv = uv + (vec2(n1, n2) - 0.5) * 0.02 * intensity;
	vec2 px = SCREEN_PIXEL_SIZE;
	vec4 c = texture(source, duv);
	vec3 rgb = c.rgb;
	rgb += texture(source, duv + vec2(px.x, 0.0)).rgb;
	rgb += texture(source, duv + vec2(0.0, px.y)).rgb;
	rgb += texture(source, duv - vec2(px.x, 0.0)).rgb;
	rgb *= 0.25;
	float luma = dot(rgb, vec3(0.299, 0.587, 0.114));
	rgb = mix(vec3(luma), rgb, 1.0 - intensity * 0.5);
	vec2 p3 = uv * 8.0 + vec2(11.0 + TIME * 0.01, 5.0);
	vec2 i3 = floor(p3);
	vec2 f3 = fract(p3);
	vec2 u3 = f3 * f3 * (3.0 - 2.0 * f3);
	float a3 = fract(sin(dot(i3, vec2(12.9898, 78.233))) * 43758.5453);
	float b3 = fract(sin(dot(i3 + vec2(1.0, 0.0), vec2(12.9898, 78.233))) * 43758.5453);
	float c3 = fract(sin(dot(i3 + vec2(0.0, 1.0), vec2(12.9898, 78.233))) * 43758.5453);
	float d3 = fract(sin(dot(i3 + vec2(1.0, 1.0), vec2(12.9898, 78.233))) * 43758.5453);
	float n3 = mix(mix(a3, b3, u3.x), mix(c3, d3, u3.x), u3.y);
	float frost = smoothstep(0.3, 0.7, n3) * intensity * 0.3;
	rgb = mix(rgb, vec3(0.9, 0.95, 1.0), frost);
	COLOR = vec4(clamp(rgb, 0.0, 1.0), c.a);
}
