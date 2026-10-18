// Screen-space sun shading from luminance normals (ADR-0023).
// Approx: normals come from a colour Sobel, not the G-buffer, so only brightness edges catch light; JAR's spell/world/player point lights and day-night sun coupling are dropped (fixed noon sun) and flat areas shade by the ambient term only.
// postfx: heavy
shader_type canvas_item;
render_mode blend_disabled;

uniform sampler2D source : hint_screen_texture, filter_nearest;
uniform float strength : hint_range(0.0, 1.0, 0.01) = 0.5;
uniform float depth_scale : hint_range(1.0, 10.0, 0.1) = 2.0;
uniform float spec_intensity : hint_range(0.0, 1.0, 0.01) = 0.3;
uniform float spec_power : hint_range(5.0, 100.0, 1.0) = 32.0;
uniform float rim_intensity : hint_range(0.0, 0.5, 0.01) = 0.15;
uniform float ambient : hint_range(0.1, 0.8, 0.01) = 0.4;

void fragment() {
	vec4 c = texture(source, SCREEN_UV);
	vec2 tx = vec2(SCREEN_PIXEL_SIZE.x, 0.0);
	vec2 ty = vec2(0.0, SCREEN_PIXEL_SIZE.y);
	float tl = dot(texture(source, SCREEN_UV - tx - ty).rgb, vec3(0.299, 0.587, 0.114));
	float tc = dot(texture(source, SCREEN_UV - ty).rgb, vec3(0.299, 0.587, 0.114));
	float tr = dot(texture(source, SCREEN_UV + tx - ty).rgb, vec3(0.299, 0.587, 0.114));
	float ml = dot(texture(source, SCREEN_UV - tx).rgb, vec3(0.299, 0.587, 0.114));
	float mr = dot(texture(source, SCREEN_UV + tx).rgb, vec3(0.299, 0.587, 0.114));
	float bl = dot(texture(source, SCREEN_UV - tx + ty).rgb, vec3(0.299, 0.587, 0.114));
	float bc = dot(texture(source, SCREEN_UV + ty).rgb, vec3(0.299, 0.587, 0.114));
	float br = dot(texture(source, SCREEN_UV + tx + ty).rgb, vec3(0.299, 0.587, 0.114));
	float dx = (tr + 2.0 * mr + br) - (tl + 2.0 * ml + bl);
	float dy = (bl + 2.0 * bc + br) - (tl + 2.0 * tc + tr);
	vec3 n = normalize(vec3(-dx * depth_scale, -dy * depth_scale, 1.0));
	vec3 sun = normalize(vec3(0.0, -0.4, 0.8));
	vec3 view = vec3(0.0, 0.0, 1.0);
	float diff = max(dot(n, sun), 0.0);
	float pw = mix(128.0, max(spec_power, 4.0), 0.5);
	vec3 halfv = normalize(sun + view);
	float spec = pow(max(dot(n, halfv), 0.0), pw) * spec_intensity;
	float rimd = 1.0 - max(dot(n, view), 0.0);
	float riml = pow(rimd, 3.0) * rim_intensity;
	float luma = dot(c.rgb, vec3(0.299, 0.587, 0.114));
	float mask = smoothstep(0.05, 0.25, luma);
	float shade = ambient + (1.0 - ambient) * diff * 0.6;
	vec3 lit = c.rgb * shade + (spec + riml * vec3(0.6, 0.7, 1.0)) * mask;
	COLOR = vec4(mix(c.rgb, lit, strength * mask), c.a);
}
