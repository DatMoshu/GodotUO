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
            // Authoritative shard minute wins, matching sun's 06:00/18:00 cycle.
            var clock=new GUO.Renderer.SpeedMode.Shadows.ShardClockState();
            clock.Set(12,0,0,1000);
            if(CloudLighting.FromMinute(clock.MinuteAt(1000))!=1
                || Math.Abs(CloudLighting.FromMinute(0)-.1f)>.0001f
                || Math.Abs(CloudLighting.FromMinute(360)-.1f)>.0001f
                || Math.Abs(CloudLighting.FromMinute(1080)-.1f)>.0001f
                || CloudLighting.FromMinute(361)>=.11f
                || CloudLighting.FromMinute(-720)!=1 || CloudLighting.FromMinute(2160)!=1)
                throw new Exception("Cloud minute cycle/noon/dawn/dusk/wrap");
            clock.Set(5,59,0,1000);
            if(clock.MinuteAt(121000)!=361 || CloudLighting.FromMinute(clock.MinuteAt(121000))<=.1f)
                throw new Exception("Clouds must advance authoritative stamp by monotonic ticks");
            if(CloudLighting.BrightnessApproximation(.35f)!=.35f
                || CloudLighting.BrightnessApproximation(float.NaN)!=0
                || CloudLighting.BrightnessApproximation(2)!=1)
                throw new Exception("Cloud named brightness approximation");
            GD.Print($"[cloud-preset] PASS bundled=3 zero-coverage=accepted rejected={rejected}");GetTree().Quit();
        }
        catch(Exception e) { GD.PrintErr("[cloud-preset] FAIL "+e);GetTree().Quit(1); }
    }
}
