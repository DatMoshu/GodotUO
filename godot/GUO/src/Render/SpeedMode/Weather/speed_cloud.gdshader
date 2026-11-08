// GUO experimental cloud plane. Procedural colour only; native art stays nearest.
shader_type canvas_item;
render_mode unshaded;
uniform vec2 target_size;
uniform vec2 camera_tile;
uniform float camera_zoom = 4.0;
uniform float camera_height = 0.0;
uniform float wind = 0.0;
uniform float seconds = 0.0;
uniform vec2 pin_pixel = vec2(-1000.0);
uniform float opacity_cap = 0.82;
uniform float daylight = 1.0;
// Integer lattice hash avoids float sin precision seams at distant map tiles.
float cloud_hash(vec2 p) {
    uvec2 q = uvec2(ivec2(p));
    uint h = q.x * 374761393u + q.y * 668265263u;
    h = (h ^ (h >> 13u)) * 1274126177u;
    return float(h ^ (h >> 16u)) / 4294967295.0;
}
float cloud_noise(vec2 p) {
    vec2 c = floor(p), f = fract(p);
    f = f*f*(3.0-2.0*f);
    return mix(mix(cloud_hash(c), cloud_hash(c+vec2(1,0)), f.x),
        mix(cloud_hash(c+vec2(0,1)), cloud_hash(c+vec2(1,1)), f.x), f.y);
}
void fragment() {
    vec2 pixel = (SCREEN_UV - 0.5) * target_size * camera_zoom;
    // Same isometric axes as the world, with gentle height-dependent parallax.
    vec2 plane = camera_tile + vec2(pixel.x+pixel.y, pixel.y-pixel.x)/44.0;
    plane += vec2(camera_height*0.015) - vec2(wind,0.25)*seconds*0.06;
    vec2 p = plane*0.055;
    float shape = cloud_noise(p)*0.6 + cloud_noise(p*2.03)*0.28 + cloud_noise(p*4.07)*0.12;
    float density = smoothstep(0.42,0.72,shape);
    float entry = smoothstep(4.0,5.0,camera_zoom);
    float above = smoothstep(80.0,240.0,camera_height);
    // daylight 1 keeps the previous day colours. Night uses the same 0.1 floor
    // as the lighting clock, so the plane does not go black.
    float sun = clamp(daylight, 0.0, 1.0);
    vec3 below_color = mix(vec3(0.08,0.09,0.12), vec3(0.64,0.69,0.74), sun);
    vec3 above_color = mix(vec3(0.10,0.11,0.16), mix(vec3(0.72,0.78,0.84),vec3(0.94,0.96,0.98),shape), sun);
    COLOR = vec4(mix(below_color,above_color,above),density*entry*opacity_cap*smoothstep(20.0,44.0,length(SCREEN_UV*target_size-pin_pixel)));
}
