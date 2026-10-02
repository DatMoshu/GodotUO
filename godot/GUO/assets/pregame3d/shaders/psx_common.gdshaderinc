// PSX look for the 3D pregame (docs/ui/pregame_3d.md). GUO, BSD-2-Clause.
// Shared by psx.gdshader and psx_cloth.gdshader. Target: Forward+ on the
// Steam Deck at a ~640x400 internal resolution (1280x800 shown 2x); a handful of meshes, so the
// cost is negligible. No variants beyond CLOTH_SWAY (one extra file).
//
// - vertex snap: clip-space x/y rounded to the internal-resolution grid (the wobble)
// - affine UVs: interpolated screen-linear, as the PS1's rasteriser did
// - nearest texture sampling, never filtered (AGENTS.md rule 7)
// - vertex lighting (render_mode below), no specular
// - a gold rim glow for a focused hotspot (rim_strength)

render_mode vertex_lighting, diffuse_lambert, specular_disabled, cull_back;

group_uniforms surface;
uniform sampler2D albedo_texture : source_color, filter_nearest, repeat_enable;
uniform vec4 albedo_color : source_color = vec4(1.0);
uniform bool use_texture = false;
uniform bool use_vertex_color = false;
// Texels below this alpha are cut (a glb with alpha-scissor leaves).
uniform float alpha_cut : hint_range(0.0, 1.0) = 0.0;
// Self-lit share of the albedo (a candle flame).
uniform float emission_strength : hint_range(0.0, 4.0) = 0.0;

group_uniforms psx;
// The SubViewport's size in pixels: vertices snap to its pixel grid.
uniform vec2 snap_resolution = vec2(640.0, 400.0);
uniform bool affine_uv = true;

group_uniforms focus;
uniform vec4 rim_color : source_color = vec4(0.878, 0.690, 0.314, 1.0); // Gold #e0b050
uniform float rim_strength : hint_range(0.0, 4.0) = 0.0;

#ifdef CLOTH_SWAY
group_uniforms cloth;
// Metres of sway at full weight, and its speed in radians per second.
uniform float sway_amount = 0.015;
uniform float sway_speed = 0.9;
// Ripple wavelength across the cloth, in metres.
uniform float sway_wavelength = 0.7;
#endif

varying vec3 affine;

void vertex() {
#ifdef CLOTH_SWAY
	// A slow ripple across the cloth's own plane; the world position keeps
	// two cloths (or two folds) out of step.
	vec3 world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	float phase = TIME * sway_speed + (world.x + world.z) * (6.2831853 / sway_wavelength);
	VERTEX += NORMAL * sin(phase) * sway_amount + vec3(0.0, cos(phase * 0.7) * sway_amount * 0.5, 0.0);
#endif
	vec4 clip = PROJECTION_MATRIX * MODELVIEW_MATRIX * vec4(VERTEX, 1.0);
	// Snap to the internal pixel grid: half the resolution per unit of NDC.
	vec2 grid = snap_resolution * 0.5;
	clip.xy = round(clip.xy / clip.w * grid) / grid * clip.w;
	POSITION = clip;
	// UV * w and w, both interpolated perspective-correct: their ratio is
	// the screen-linear (affine) UV.
	affine = vec3(UV * clip.w, clip.w);
}

void fragment() {
	vec2 uv = affine_uv ? affine.xy / affine.z : UV;
	vec4 c = albedo_color;
	if (use_texture) {
		c *= texture(albedo_texture, uv);
	}
	if (use_vertex_color) {
		c *= COLOR;
	}
	if (c.a < alpha_cut) {
		discard;
	}
	ALBEDO = c.rgb;
	ROUGHNESS = 1.0;
	float rim = pow(1.0 - clamp(dot(NORMAL, VIEW), 0.0, 1.0), 2.0);
	EMISSION = rim_color.rgb * (rim * rim_strength + 0.08 * rim_strength) + c.rgb * emission_strength;
}
