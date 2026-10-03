using GUO.Platform.Android;
using System.Text.Json;

int checks = 0;
void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
bool Overlap(LayoutRect a, LayoutRect b) => !a.Empty && !b.Empty && a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;
void Validate(DeviceLayout layout, int w, int h)
{
    Check(layout.World.Width > 0 && layout.World.Height > 0, "World must remain visible");
    var regions = new[] { layout.World, layout.Companion, layout.Movement, layout.Actions, layout.Hinge };
    foreach (var r in regions)
        Check(r.X >= 0 && r.Y >= 0 && r.Right <= w && r.Bottom <= h && r.Width >= 0 && r.Height >= 0, $"Outside window: {r}");
    for (int i = 0; i < regions.Length; i++) for (int j = i + 1; j < regions.Length; j++)
        Check(!Overlap(regions[i], regions[j]), $"Overlapping panes: {regions[i]} / {regions[j]}");
}
foreach (int w in new[] { 320, 360, 600, 840, 1080, 2076 })
foreach (int h in new[] { 320, 640, 800, 1557, 2152 })
foreach (float density in new[] { .5f, 1f, 2f })
foreach (bool controls in new[] { true, false })
foreach (DevicePosture p in Enum.GetValues<DevicePosture>())
    Validate(DeviceLayout.Resolve(w, h, density, p, default, false, controls), w, h);
var horizontal = DeviceLayout.Resolve(1000, 900, 1, DevicePosture.Auto, new(0, 420, 1000, 24), true, true);
Check(horizontal.Posture == DevicePosture.Tabletop && horizontal.World.Bottom == 412 && horizontal.Companion.Y == 452, "Use actual off-centre hinge bounds");
Validate(horizontal, 1000, 900);
var vertical = DeviceLayout.Resolve(1000, 900, 1, DevicePosture.Flat, new(460, 0, 26, 900), true, true);
Check(vertical.Posture == DevicePosture.Book && vertical.World.Right < 460 && vertical.Companion.X > 486, "A separating flat hinge overrides spanning layout");
Validate(vertical, 1000, 900);
var stale = DeviceLayout.Resolve(360, 800, 1, DevicePosture.Auto, new(900, 0, 10, 1000), true, true);
Check(stale.Posture == DevicePosture.Phone, "Ignore stale rotated geometry");
Check(DeviceLayout.Resolve(800, 1280, 1, DevicePosture.Auto, default, false, true).Posture == DevicePosture.Tablet, "Tablet uses dp, not raw pixels");
Check(DeviceLayout.Resolve(1080, 2400, .333f, DevicePosture.Auto, default, false, true).Posture == DevicePosture.Phone, "High density phone is not a tablet");
var tent = DeviceLayout.Resolve(960, 600, 1, DevicePosture.Tent, default, false, false);
Check(tent.Companion.Empty && tent.Movement.Empty && tent.World.Height == 600, "Tent with pad uses visible display only");
Console.WriteLine($"PASS: {checks} geometry assertions including rotations, hinge occlusion, tablet density and controller layouts.");
if (args.Length > 0)
{
    var cases = new[] {
        ("phone", "Phone landscape", 840, 390, DevicePosture.Phone, default(LayoutRect), false, true),
        ("cover", "Fold closed / cover", 390, 840, DevicePosture.Phone, default(LayoutRect), false, true),
        ("flat", "Fold open / flat", 800, 840, DevicePosture.Flat, default(LayoutRect), false, true),
        ("tabletop", "Fold / tabletop", 800, 840, DevicePosture.Tabletop, new LayoutRect(0, 408, 800, 24), true, true),
        ("book", "Fold / book", 900, 760, DevicePosture.Book, new LayoutRect(438, 0, 24, 760), true, true),
        ("tent", "Tent / external controller", 840, 520, DevicePosture.Tent, default(LayoutRect), false, false),
        ("tablet-landscape", "Tablet landscape", 1280, 800, DevicePosture.Tablet, default(LayoutRect), false, true),
        ("tablet-portrait", "Tablet portrait", 800, 1280, DevicePosture.Tablet, default(LayoutRect), false, true)
    };
    var output = cases.Select(c => new { id = c.Item1, title = c.Item2, width = c.Item3, height = c.Item4,
        layout = DeviceLayout.Resolve(c.Item3,c.Item4,1,c.Item5,c.Item6,c.Item7,c.Item8) });
    File.WriteAllText(args[0], JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
}
