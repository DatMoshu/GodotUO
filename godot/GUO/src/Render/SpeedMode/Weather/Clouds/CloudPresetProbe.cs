// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Godot;

namespace GUO.Renderer.SpeedMode.Weather;

/// <summary>Strict parser regression, no renderer or client data required.</summary>
public partial class CloudPresetProbe : Node
{
    internal static readonly string[] Presets = { "soft-billowing", "thin-haze", "broken-storm" };
    internal const string Folder = "res://src/Render/SpeedMode/Weather/Clouds/";
    internal static string Json(string name) => Godot.FileAccess.GetFileAsString(Folder+name+".json");
    public override void _Ready()
    {
        try {
            int rejected=0;
            foreach (string name in Presets) {
                string json=Json(name);
                var preset=CloudPreset.Parse(Encoding.UTF8.GetBytes(json));
                if(preset.Layers.Length<1) throw new Exception("Empty accepted preset");
            }
            string valid=Json(Presets[0]);
            void Bad(string json) {
                try { CloudPreset.Parse(Encoding.UTF8.GetBytes(json)); }
                catch(InvalidDataException) { rejected++;return; }
                throw new Exception("Invalid cloud accepted: "+json);
            }
            void Edit(Action<JsonNode> change) {
                var node=JsonNode.Parse(valid);change(node);Bad(node.ToJsonString());
            }
            Bad(valid.Replace("\"schema\":", "\"schema\":\"guo/cloud@1\",\"schema\":"));
            Bad("null");Bad("[]");Bad("{}");Bad(new string(' ',CloudPreset.MaxBytes+1));
            Edit(n=>n["schema"]="guo/cloud@2");
            Edit(n=>n["unknown"]=1);Edit(n=>n.AsObject().Remove("name"));
            Edit(n=>n["zoom_end"]=4);Edit(n=>n["zoom_start"]=3.9);
            Edit(n=>n["layers"]=new JsonArray());
            Edit(n=>n["layers"].AsArray().Add(n["layers"][0].DeepClone()));
            Edit(n=>n["layers"][0]["algorithm"]="external-code");
            Edit(n=>n["layers"][0]["sprite"]="../escape.png");
            Edit(n=>n["layers"][0]["sprite"]="C:/escape.png");
            Edit(n=>n["layers"][0]["sprite"]="a\\b.png");
            Edit(n=>n["layers"][0]["sprite"]="cloud.gdshader");
            Edit(n=>n["layers"][0]["sprite"]="");
            Edit(n=>n["layers"][0]["density"]="1");
            Edit(n=>n["layers"][0]["coverage"]=-0.1);
            Edit(n=>n["layers"][0]["scale"]=0);
            Edit(n=>n["layers"][0]["wind_speed"]=3);
            Edit(n=>n["layers"][0]["day_color"]=new JsonArray(1,1));
            Edit(n=>n["layers"][0]["day_color"][0]=1.1);
            Edit(n=>n["layers"][0]["height"]=601);
            Edit(n=>n["layers"][0]["parallax"]=-0.1);
            Edit(n=>n["layers"][0]["opacity"]=2);
            Edit(n=>n["layers"][0]["mask"]="unknown");
            Edit(n=>n["layers"][0]["extra"]=0);
            var zero=JsonNode.Parse(valid);zero["layers"][0]["coverage"]=0;
            CloudPreset.Parse(Encoding.UTF8.GetBytes(zero.ToJsonString()));
            try { CloudPreset.Parse(new byte[]{0xff});throw new Exception("Invalid UTF-8 accepted"); }
            catch(InvalidDataException) { rejected++; }
            // Day/night consumes the same shard resolver result as sun shadows.
            var clock=new GUO.Renderer.SpeedMode.Shadows.ShardClockState();
            clock.Set(12,0,0,1000);
            var noon=GUO.Renderer.SpeedMode.Shadows.SunShadowProjection.Resolve(clock,.2f,1000);
            clock.Set(0,0,0,1000);
            var midnight=GUO.Renderer.SpeedMode.Shadows.SunShadowProjection.Resolve(clock,1,1000);
            float noonLight=CloudLighting.Daylight(noon.Strength,noon.Length,noon.Source,.2f);
            float nightLight=CloudLighting.Daylight(midnight.Strength,midnight.Length,midnight.Source,1);
            if(noonLight<=nightLight || noonLight!=1 || Math.Abs(nightLight-.1f)>.0001f)
                throw new Exception("Cloud lighting disagrees with sun clock");
            if(CloudLighting.Daylight(.55f,1,"local-test-clock",0)!=1
                || CloudLighting.Daylight(.2f,1,"local-test-clock",1)!=.1f
                || CloudLighting.Daylight(.6f,4,"shard-brightness-approximation",.35f)!=.35f)
                throw new Exception("Cloud test-clock/fallback provenance mapping");
            GD.Print($"[cloud-preset] PASS bundled=3 zero-coverage=accepted rejected={rejected}");GetTree().Quit();
        }
        catch(Exception e) { GD.PrintErr("[cloud-preset] FAIL "+e);GetTree().Quit(1); }
    }
}
