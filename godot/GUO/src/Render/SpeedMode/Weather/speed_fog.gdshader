// SPDX-License-Identifier: BSD-2-Clause
// ADR-0048: one nearest surface sample; low outdoor mist in continuous world space.
shader_type canvas_item;
render_mode unshaded;
uniform sampler2D surface_buffer : filter_nearest, repeat_disable;
uniform vec2 surface_origin;
uniform vec3 camera_world;
uniform float fog_density = 0.0;
uniform float wind = 0.0;
uniform float seconds = 0.0;

float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1,311.7))) * 43758.5453); }
float noise(vec2 p) {
    vec2 cell = floor(p), f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(cell), hash(cell + vec2(1,0)), f.x),
        mix(hash(cell + vec2(0,1)), hash(cell + vec2(1,1)), f.x), f.y);
}
void fragment() {
    vec4 sample_data = texture(surface_buffer, SCREEN_UV);
    int data = int(round(sample_data.a));
    int kind = data & 15;
    vec3 point = vec3(sample_data.xy + surface_origin, sample_data.z);
    float distance_tiles = length(point.xy - camera_world.xy);
    // UO height units: twelve units above the camera attenuates mist by e^-1.
    float height_falloff = exp(-max(point.z - camera_world.z, 0.0) / 12.0);
    vec2 drift = vec2(wind, 0.2) * seconds * 0.12;
    float variation = mix(0.6, 1.0, noise(point.xy * 0.25 - drift));
    // At full density, ten tiles gives at most 30% opacity; keep native art readable.
    float alpha = min(0.45, 1.0 - exp(-distance_tiles * 0.035 * fog_density * height_falloff * variation));
    if (kind == 0 || (data & 16) != 0) alpha = 0.0;
    COLOR = vec4(0.58, 0.63, 0.68, alpha);
}
