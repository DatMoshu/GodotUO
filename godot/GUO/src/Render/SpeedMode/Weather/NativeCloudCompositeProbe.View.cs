// SPDX-License-Identifier: BSD-2-Clause
// Task1057 final-output view contracts on disconnected native GameScene.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using GUO.Game.Scenes;

namespace GUO.Renderer.SpeedMode.Weather;

public partial class NativeCloudCompositeProbe
{
    private async Task QualifyNativeView()
    {
        var cities = WorldOverview.Cities;
        var world = _game.UO.World;
        uint tick = Time.Ticks;
        try
        {
            WorldOverview.Cities = new[] { new CityInfo(0, "Britain", "", "Disconnected fixture marker", 1496, 1628, 10, 0, true) };
            _profile.LightingSpeedMode = true; _profile.ExperimentalWorldOverview = true;
            Zoom(6); await NativeWarm(40);
            var off = await Capture("native-overview-reset", true);
            var on = await Capture("native-overview-cloud");
            using var before = Image.CreateFromData(1280, 720, false, off.FinalFormat, off.Final);
            using var after = Image.CreateFromData(1280, 720, false, on.FinalFormat, on.Final);
            var player = world.Player; var maps = _game.UO.FileManager.Maps;
            var transform = WorldOverview.MapTransform(_scene.Camera, Field<GUO.Compat.Point>(_scene, "_offset"), player.Z,
                maps.MapBlocksSize[world.Map.Index, 0] * 8, maps.MapBlocksSize[world.Map.Index, 1] * 8);
            var pin = transform * new Vector2(player.X + .5f, player.Y + .5f) + new Vector2(0, -32);
            int changed = 0;
            for (int y = -9; y <= 9; y++) for (int x = -9; x <= 9; x++)
            {
                if (x*x + y*y > 81) continue;
                int px = (int)pin.X + x, py = (int)pin.Y + y;
                if (px >= 0 && py >= 0 && px < 1280 && py < 720 && before.GetPixel(px, py) != after.GetPixel(px, py)) changed++;
            }
            Require(changed == 0, "actual final-output pin head changed");
            var bounds = Field<List<Rect2>>(_scene.Overview, "_labelBounds");
            var names = Field<HashSet<string>>(_scene.Overview, "_labelNames");
            Require(names.Contains("Britain") && bounds.Count > 1, "actual city label missing");
            var label = bounds[1]; int ink = 0, retained = 0;
            var rect = new Rect2I((int)label.Position.X, (int)label.Position.Y, (int)label.Size.X, (int)label.Size.Y);
            using var a = before.GetRegion(rect); using var b = after.GetRegion(rect);
            Save(a, "native-final-label-reset"); Save(b, "native-final-label-cloud");
            for (int y = 3; y < rect.Size.Y - 2; y++) for (int x = 4; x < rect.Size.X - 4; x++)
            {
                var p = a.GetPixel(x, y); var q = b.GetPixel(x, y); float l = (p.R + p.G + p.B) / 3;
                if (l > .55f) { ink++; if ((q.R + q.G + q.B) / 3 >= l * .65f) retained++; }
            }
            Require(ink > 5 && retained >= ink * .95f, "actual final label contrast not retained");
            _frames.Add(new { test = "native-final-pin-label", pinChanged = changed, ink, retained }); Results();

            Zoom(5); await NativeWarm();
            var originalX = player.X; var originalY = player.Y;
            await Capture("native-pan-before");
            player.SetInWorldTile((ushort)(originalX + 2), (ushort)(originalY + 1), player.Z);
            await NativeWarm(); await Capture("native-pan-shift");
            var pass = Field<SpeedModeCloudPass>(_scene, "_speedCloudPass");
            var origin = (Vector2I)typeof(GameScene).GetMethod("SpeedModeCameraOrigin", Private).Invoke(_scene, null);
            var expected = new Vector2(origin.X, origin.Y);
            var actual = ((ShaderMaterial)pass.Material).GetShaderParameter("camera_tile").AsVector2();
            Require(expected == actual, "native pan cloud coordinate binding differs from scene camera origin");
            player.SetInWorldTile(originalX, originalY, player.Z); await NativeWarm();
            await Capture("native-pan-return");
            _profile.WindStrength = 1;
            using var cloudBefore = Field<RenderTarget2D>(_scene, "_speedCloudTarget").Texture.GetImage();
            Time.Ticks += 90000; await NativeWarm(); await Capture("native-time-wind");
            using var cloudAfter = Field<RenderTarget2D>(_scene, "_speedCloudTarget").Texture.GetImage();
            Require(!cloudBefore.GetData().AsSpan().SequenceEqual(cloudAfter.GetData()), "native time/wind did not move cloud field");
            _frames.Add(new { test = "native-pan-time", cameraOriginMatched = true, windChanged = true }); Results();
        }
        finally { WorldOverview.Cities = cities; Time.Ticks = tick; _profile.WindStrength = 0; }
    }
}
