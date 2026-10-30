// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned: shared light-pass shadow functions, ADR-0048 amendment.
namespace GUO.Renderer.SpeedMode.Shadows;

internal static class ShadowSampling
{
    // XY/radius in tiles; Z and top in UO units. Normal XY points toward the
    // receiver's exposed face. Bind nearest; interpolate only the angle axis.
    internal const string Glsl="""
        float speed_lamp_shadow(sampler2D polar_map, vec3 relative_position, vec2 normal_xy,
                                float lamp_z, int row, float softness_tenths) {
            if (row<0 || row>=64) return 1.0;
            vec3 p=relative_position+vec3(normal_xy*0.05,0.0);
            float distance_to_pixel=length(p.xy);
            if (distance_to_pixel<0.001) return 1.0;
            float angle=(atan(p.y,p.x)/6.28318530718+0.5)*256.0-0.5;
            int lo=int(floor(angle)); float weight=fract(angle);
            vec2 a=texelFetch(polar_map,ivec2((lo+256)%256,row),0).rg;
            vec2 b=texelFetch(polar_map,ivec2((lo+257)%256,row),0).rg;
            float result=0.0;
            for (int i=0;i<2;i++) {
                vec2 blocker=i==0 ? a : b;
                float ray_z=lamp_z+p.z*blocker.x/distance_to_pixel;
                float penumbra=max(0.001,softness_tenths*0.1);
                float blocked=smoothstep(blocker.x+0.2,blocker.x+0.2+penumbra,distance_to_pixel);
                blocked*=1.0-smoothstep(blocker.y,blocker.y+0.5,ray_z);
                result+=(1.0-0.85*blocked)*(i==0 ? 1.0-weight : weight);
            }
            return result;
        }
        float speed_sun_shadow(sampler2D sun_mask,ivec2 pixel,int surface_class) {
            return surface_class==1 ? 1.0-clamp(texelFetch(sun_mask,pixel,0).r,0.0,1.0) : 1.0;
        }
        """;
}
