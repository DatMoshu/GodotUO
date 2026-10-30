// GUO cloud@1: bounded two-layer 2D plane, no volumetric claim.
// Nearest sampling. Two layers; at most 18 sprite samples + six noise octaves/pixel.
shader_type canvas_item;
render_mode unshaded;
uniform vec2 target_size;
uniform vec2 camera_tile;
uniform float camera_zoom;
uniform float camera_height;
uniform float seconds;
uniform float wind;
uniform float daylight = 1.0;
uniform vec2 pin_pixel = vec2(-1000.0);
uniform float opacity_cap = 0.82;
uniform vec2 zoom_blend = vec2(4.0,5.0);
uniform int layer_count = 1;
// algorithm, mask, density, coverage
uniform vec4 shape0;
uniform vec4 shape1;
// scale, wind radians, wind speed, opacity
uniform vec4 motion0;
uniform vec4 motion1;
// height, parallax, day/night response
uniform vec3 altitude0;
uniform vec3 altitude1;
uniform vec3 day0;
uniform vec3 day1;
uniform vec3 night0;
uniform vec3 night1;
uniform sampler2D sprite0 : filter_nearest, repeat_disable;
uniform sampler2D sprite1 : filter_nearest, repeat_disable;

float hash_cloud(vec2 p) {
    uvec2 q = uvec2(ivec2(p));
    uint h = q.x * 374761393u + q.y * 668265263u;
    h = (h ^ (h >> 13u)) * 1274126177u;
    return float(h ^ (h >> 16u)) / 4294967295.0;
}
float noise_cloud(vec2 p) {
    vec2 c=floor(p), f=fract(p); f=f*f*(3.0-2.0*f);
    return mix(mix(hash_cloud(c),hash_cloud(c+vec2(1,0)),f.x),
        mix(hash_cloud(c+vec2(0,1)),hash_cloud(c+vec2(1,1)),f.x),f.y);
}
vec4 cloud_layer(vec2 world, vec4 shape, vec4 motion, vec3 altitude,
    vec3 day_color, vec3 night_color, sampler2D sprite_tex) {
    vec2 direction=vec2(cos(motion.y),sin(motion.y));
    // Phase computed on CPU without hourly wrapping; local height shifts planes only.
    vec2 plane=world+vec2((altitude.x-camera_height)*altitude.y)
        - direction*motion.z*seconds*wind;
    vec2 p=plane*motion.x;
    float noise_value=noise_cloud(p)*0.6+noise_cloud(p*2.03)*0.28+noise_cloud(p*4.07)*0.12;
    float threshold=1.0-shape.w;
    float procedural=smoothstep(threshold,threshold+0.25,noise_value);
    if (shape.y > 0.5 && shape.y < 1.5)
        procedural=smoothstep(threshold,threshold+0.65,noise_cloud(p*vec2(0.35,2.0)))*0.65;
    if (shape.y > 1.5)
        procedural=smoothstep(threshold,threshold+0.14,noise_value)*smoothstep(0.25,0.6,noise_cloud(p*0.45));
    float sprite_alpha=0.0;
    if (shape.x>0.5) {
        // Neighbor contributions avoid exposing a regular lattice of sprites.
        for(int y=-1;y<=1;y++) for(int x=-1;x<=1;x++) {
            vec2 cell=floor(p)+vec2(float(x),float(y));
            float seed=hash_cloud(cell+vec2(31,97));
            vec2 center=cell+vec2(0.15)+vec2(seed,hash_cloud(cell+vec2(71,13)))*0.7;
            float angle=hash_cloud(cell+vec2(11,59))*6.283185;
            vec2 q=p-center;
            q=vec2(cos(angle)*q.x-sin(angle)*q.y,sin(angle)*q.x+cos(angle)*q.y);
            vec2 extent=vec2(0.9+seed*0.9,0.85+hash_cloud(cell+vec2(5,23))*0.8);
            vec2 uv=q/extent+0.5;
            float alpha=texture(sprite_tex,clamp(uv,vec2(0),vec2(1))).a;
            alpha*=step(0.0,uv.x)*step(uv.x,1.0)*step(0.0,uv.y)*step(uv.y,1.0)*step(seed,shape.w);
            sprite_alpha=max(sprite_alpha,alpha);
        }
    }
    float mask=procedural;
    if (shape.x>0.5 && shape.x<1.5) mask=sprite_alpha;
    if (shape.x>1.5) mask=sprite_alpha*mix(0.3,1.0,procedural);
    // Coverage zero always means transparent, independent of the noise threshold.
    float alpha=clamp(mask*shape.z,0.0,1.0)*motion.w*step(0.00001,shape.w);
    float light=mix(1.0,clamp(daylight,0.0,1.0),altitude.z);
    vec3 color=mix(night_color,day_color,light)*mix(0.82,1.0,noise_value);
    return vec4(color,alpha);
}
void fragment() {
    vec2 pixel=(SCREEN_UV-0.5)*target_size*camera_zoom;
    vec2 world=camera_tile+vec2(pixel.x+pixel.y,pixel.y-pixel.x)/44.0;
    vec4 a=cloud_layer(world,shape0,motion0,altitude0,day0,night0,sprite0);
    if (layer_count>1) {
        vec4 b=cloud_layer(world,shape1,motion1,altitude1,day1,night1,sprite1);
        float alpha=b.a+a.a*(1.0-b.a);
        a=vec4((b.rgb*b.a+a.rgb*a.a*(1.0-b.a))/max(alpha,0.00001),alpha);
    }
    float entry=smoothstep(zoom_blend.x,zoom_blend.y,camera_zoom);
    float pin=smoothstep(20.0,44.0,length(SCREEN_UV*target_size-pin_pixel));
    COLOR=vec4(a.rgb,min(a.a,opacity_cap)*entry*pin);
}
