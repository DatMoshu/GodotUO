// GUO UO+ sway. Canvas Compatibility/Forward+, four strips, no added texture reads.
shader_type canvas_item;
render_mode blend_premul_alpha, unshaded;
#include "uo_hue_core.gdshaderinc"

uniform float sway_wind = 0.0;
uniform float sway_time = 0.0;
// Shared screen-space wind direction: mostly sideways, a little depth.
uniform vec2 sway_direction = vec2(1.0, -0.12);

void vertex() {
    packed_hue = COLOR;
    land_normal = vec3(0.0, 0.0, 1.0);
    float phase = CUSTOM0.y;
    float gust = 0.75 + 0.25 * sin(sway_time * 0.37);
    float wave = sin(sway_time * 1.7 + phase) + 0.22 * sin(sway_time * 2.9 + phase * 1.3);
    VERTEX += sway_direction * (CUSTOM0.x * CUSTOM0.z * sway_wind * gust * wave);
}

void fragment() {
    COLOR = uo_shade(texture(TEXTURE, UV), SCREEN_UV, SCREEN_PIXEL_SIZE);
}
