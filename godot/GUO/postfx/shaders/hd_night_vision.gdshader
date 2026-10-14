// HD night vision (HD light Phase 5): green phosphor that cooperates with
// the GI instead of fighting it.
//
// The classic night_vision pass phosphors the raw source and ignores the
// light buffer, so under HD it fights the torch composite: phosphor the
// unlit albedo, then the separate light draw darkens it afterwards. This
// pass instead samples the LIT buffer like hd_tonemap does — world x torch
// light with the same no-light guard — and phosphors THAT, so torch pools,
// sun moonlight and GI bounce all survive into the phosphor. When an enabled
// hd_gi pass composites above zero the stack pushes hd_skip_light (same as
// hd_tonemap) and the multiply is skipped because source already carries
// the GI composite. Order it after hd_sun/hd_gi (see the HD Night Vision
// preset): moonlight and bounce feed the phosphor gain.
//
// intensity 0 is the lit composite (useful as a night gain stage);
// intensity 1 is full phosphor. Nearest throughout; the world draw and the
// light buffer are untouched.
shader_type canvas_item;
render_mode blend_disabled;

uniform sampler2D source : hint_screen_texture, filter_nearest;
uniform sampler2D light_tex : filter_nearest;
uniform float intensity : hint_range(0.0, 1.0, 0.01) = 1.0;
// Phosphor gain: the lit composite at midnight is ~0.08-0.12 (moon floor x
// dark albedo, floored so HD Light stays dimly visible), so a daylight gain
// would show black. 12x lifts torch pools and
// moonlit edges into readable green while keeping unlit ground dark; noon
// blows out (it is a night tool). Was 1.6, which left midnight black.
uniform float gain : hint_range(0.5, 24.0, 0.5) = 12.0;
// Pushed per-frame by the PostFx stack while an enabled hd_gi pass
// composites above zero (no hint so the menu hides it).
uniform float hd_skip_light = 0.0;

void fragment() {
	vec4 c = texture(source, SCREEN_UV);
	vec4 lt = texture(light_tex, SCREEN_UV);
	// The lit composite, same guard as hd_tonemap: no light override means
	// no darkening; GI already composited means multiply by 1.
	vec3 mult = hd_skip_light > 0.5 ? vec3(1.0) : (lt.a < 0.01 ? vec3(1.0) : lt.rgb);
	vec3 lit = c.rgb * mult;
	// Phosphor from the LIT buffer (this is the cooperation: torch pools and
	// moonlight raise the phosphor instead of being darkened after it).
	float l = dot(lit, vec3(0.299, 0.587, 0.114)) * gain;
	float s = sin(SCREEN_UV.y * 800.0) * 0.05;
	float n = fract(sin(dot(SCREEN_UV + fract(TIME * 0.1), vec2(12.9898, 78.233))) * 43758.5453) * 0.1;
	vec2 ct = SCREEN_UV - 0.5;
	float v = 1.0 - dot(ct, ct) * 2.0;
	vec3 nv = vec3(0.1, l * 1.5 + s + n, 0.1) * v;
	COLOR = vec4(mix(lit, nv, clamp(intensity, 0.0, 1.0)), c.a);
}
