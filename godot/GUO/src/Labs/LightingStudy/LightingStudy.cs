// SPDX-License-Identifier: BSD-2-Clause
using System;

namespace GUO.Labs.LightingStudy;

internal static class LightingStudy
{
    internal static bool Enabled = Environment.GetEnvironmentVariable("GUO_LIGHTING_STUDY") == "1";

    // Inputs are GUO's existing surface classes and shadow direction. The
    // artistic R8 shadow mask already includes strength, so use it once.
    internal const string AmbientGlsl = """
        if(pc.world_radius.w>0.5 && pc.screen_mode.w==0.0 && kind!=0) {
            float daylight=smoothstep(0.18,0.65,pc.world_radius.z);
            vec3 ambient=pc.ambient_roof.rgb;
            // Reduce the added night fill under cover.
            const vec3 nightFill=vec3(0.20,0.22,0.26);
            float nightExposure=(packed&16)!=0 ? 0.5 : 1.0;
            ambient+=max(nightFill-ambient,vec3(0))*nightExposure*(1.0-daylight);

            // Wall normals match the point-light pass. Other art retains its
            // baked orientation and the wall response fades out after dusk.
            vec2 towardLight=pc.world_radius.xy;
            float facing=1.0;
            if(kind==2) facing=max(0.0,towardLight.y);
            else if(kind==3) facing=max(0.0,towardLight.x);

            float wallDirection=mix(1.0,mix(0.72,1.0,facing),daylight);

            // Diffuse sky retains half the light lost in the old shadow.
            float visibility=mix(1.0,sun,0.5);
            light=vec4(ambient*(roof*wallDirection*visibility),1);
            return;
        }

        """;
}
