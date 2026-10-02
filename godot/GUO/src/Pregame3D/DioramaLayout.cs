// SPDX-License-Identifier: BSD-2-Clause
// GUO addition, not a port.

using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;

namespace GUO.Pregame3D;

/// <summary>Where one thing stands: position, rotation in degrees, scale.</summary>
internal struct Placement
{
    public Vector3 Position;
    public Vector3 RotationDeg;
    public Vector3 Scale;

    public Placement(Vector3 position, Vector3 rotationDeg = default, Vector3? scale = null)
    {
        Position = position;
        RotationDeg = rotationDeg;
        Scale = scale ?? Vector3.One;
    }

    public Transform3D Transform
    {
        get
        {
            var basis = Basis.FromEuler(new Vector3(
                Mathf.DegToRad(RotationDeg.X), Mathf.DegToRad(RotationDeg.Y), Mathf.DegToRad(RotationDeg.Z)));

            return new Transform3D(basis * Basis.FromScale(Scale == Vector3.Zero ? Vector3.One : Scale), Position);
        }
    }
}

/// <summary>
/// An object shown as UI ("ui_anchors"): squared to the camera, its screen
/// box in a corner of the viewport with a margin, a fixed share of the
/// viewport's height tall, at a depth along the camera's forward.
/// </summary>
internal struct UiAnchor
{
    /// <summary>"bottom_left", "bottom_right", "top_left" or "top_right".</summary>
    public string Corner;

    /// <summary>The gap to the corner, in pixels of <see cref="ReferenceHeight"/> (scaled with the viewport's height).</summary>
    public Vector2 MarginPx;
    public float ReferenceHeight;
    public float HeightFrac;
    public float DepthM;
}

internal struct LightSpec
{
    public string Type;
    public Vector3 Position;
    public Vector3 RotationDeg;
    public Color Color;
    public float Energy;
    public float Range;
    public bool Flicker;
}

/// <summary>
/// The diorama's composition: <c>res://assets/pregame3d/layout.json</c> when
/// present (the art's own, schema in docs/ui/pregame_3d.md), over built-in
/// defaults that place the placeholder primitives. A key the file leaves out
/// keeps its default; a malformed file is logged once and ignored.
/// </summary>
internal sealed class DioramaLayout
{
    public const string Path = "res://assets/pregame3d/layout.json";

    public Vector3 CameraPosition = new(0f, 2.35f, 2.45f);
    public Vector3 CameraLookAt = new(0f, 0.42f, -0.05f);
    public float FovDeg = 46f;
    public float LidOpenDeg = -108f;
    public float LidClosedDeg = 0f;
    public bool FromFile;

    /// <summary>The scene's ambient light colour ("ambient_color"), or null for the built-in.</summary>
    public Color? AmbientColor;

    /// <summary>"file.glb#Node" to its UI anchor, from "ui_anchors".</summary>
    public readonly Dictionary<string, UiAnchor> UiAnchors = new();

    /// <summary>Per step ("login", "servers", "characters"): the camera, from "step_cameras".</summary>
    public readonly Dictionary<string, (Vector3 position, Vector3 lookAt, float fovDeg)> StepCameras = new();

    /// <summary>"file.glb#Node" (or "file.glb") to its placement, from the file only.</summary>
    public readonly Dictionary<string, Placement> Objects = new();

    public List<LightSpec> Lights = new()
    {
        new LightSpec { Type = "omni", Position = new(0f, 2.4f, 1.6f), Color = new Color(1f, 0.86f, 0.66f), Energy = 1.6f, Range = 7f, Flicker = false },
        new LightSpec { Type = "omni", Position = new(-1.25f, 0.75f, -0.35f), Color = new Color(1f, 0.62f, 0.3f), Energy = 1.5f, Range = 3.2f, Flicker = true },
        new LightSpec { Type = "omni", Position = new(1.25f, 0.75f, -0.35f), Color = new Color(1f, 0.62f, 0.3f), Energy = 1.5f, Range = 3.2f, Flicker = true },
        new LightSpec { Type = "omni", Position = new(-2.1f, 1.95f, -1.3f), Color = new Color(1f, 0.55f, 0.25f), Energy = 2.2f, Range = 4f, Flicker = true },
    };

    public List<Placement> ScrollSlots = new()
    {
        new(new Vector3(-0.42f, 0.2f, -0.28f), new Vector3(0f, 0f, 0f)),
        new(new Vector3(0.42f, 0.2f, -0.28f)),
        new(new Vector3(-0.42f, 0.2f, 0.12f)),
        new(new Vector3(0.42f, 0.2f, 0.12f)),
        new(new Vector3(-0.42f, 0.2f, 0.42f)),
        new(new Vector3(0.42f, 0.2f, 0.42f)),
    };

    public List<Placement> PlinthSlots = new()
    {
        new(new Vector3(-0.66f, 0.12f, -0.3f)),
        new(new Vector3(-0.22f, 0.12f, -0.3f)),
        new(new Vector3(0.22f, 0.12f, -0.3f)),
        new(new Vector3(0.66f, 0.12f, -0.3f)),
        new(new Vector3(-0.44f, 0.12f, 0.22f)),
        new(new Vector3(0f, 0.12f, 0.22f)),
        new(new Vector3(0.44f, 0.12f, 0.22f)),
    };

    public List<Placement> CardSlots = new()
    {
        new(new Vector3(-0.75f, 0.02f, 1.05f), new Vector3(0f, 8f, 0f)),
        new(new Vector3(-0.25f, 0.02f, 1.1f), new Vector3(0f, -4f, 0f)),
        new(new Vector3(0.25f, 0.02f, 1.08f), new Vector3(0f, 5f, 0f)),
        new(new Vector3(0.75f, 0.02f, 1.04f), new Vector3(0f, -7f, 0f)),
    };

    /// <summary>The three option studs (Autologin, Save account, Music); "stud_slots" in the file.</summary>
    public List<Placement> StudSlots = new()
    {
        new(new Vector3(-0.62f, 0.3f, 0.63f), new Vector3(90f, 0f, 0f)),
        new(new Vector3(-0.1f, 0.3f, 0.63f), new Vector3(90f, 0f, 0f)),
        new(new Vector3(0.42f, 0.3f, 0.63f), new Vector3(90f, 0f, 0f)),
    };

    public static DioramaLayout Load()
    {
        var layout = new DioramaLayout();

        if (!FileAccess.FileExists(Path))
        {
            GD.Print($"[GUO] pregame3d: no {Path}; using the built-in placement");
            return layout;
        }

        try
        {
            string text = FileAccess.GetFileAsString(Path);
            using JsonDocument doc = JsonDocument.Parse(text);
            JsonElement root = doc.RootElement;
            layout.FromFile = true;

            if (root.TryGetProperty("camera", out JsonElement cam))
            {
                layout.CameraPosition = Vec(cam, "position", layout.CameraPosition);
                layout.CameraLookAt = Vec(cam, "look_at", layout.CameraLookAt);

                if (cam.TryGetProperty("fov_deg", out JsonElement fov) && fov.ValueKind == JsonValueKind.Number)
                {
                    layout.FovDeg = (float) fov.GetDouble();
                }
            }

            if (root.TryGetProperty("objects", out JsonElement objects) && objects.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty p in objects.EnumerateObject())
                {
                    layout.Objects[p.Name] = Place(p.Value, default);
                }
            }

            if (root.TryGetProperty("lid_open_deg", out JsonElement lid) && lid.ValueKind == JsonValueKind.Number)
            {
                layout.LidOpenDeg = (float) lid.GetDouble();
            }

            layout.LidClosedDeg = Num(root, "lid_closed_deg", 0f);

            if (root.TryGetProperty("ambient_color", out JsonElement _))
            {
                layout.AmbientColor = ColorOf(root, "ambient_color", new Color(0.32f, 0.24f, 0.2f));
            }

            if (root.TryGetProperty("step_cameras", out JsonElement steps) && steps.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty p in steps.EnumerateObject())
                {
                    layout.StepCameras[p.Name] = (Vec(p.Value, "position", layout.CameraPosition), Vec(p.Value, "look_at", layout.CameraLookAt), Num(p.Value, "fov_deg", layout.FovDeg));
                }
            }

            if (root.TryGetProperty("ui_anchors", out JsonElement anchors) && anchors.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty p in anchors.EnumerateObject())
                {
                    JsonElement a = p.Value;
                    Vector3 refViewport = a.TryGetProperty("reference_viewport", out JsonElement rv) && rv.ValueKind == JsonValueKind.Array && rv.GetArrayLength() >= 2
                        ? new Vector3((float) rv[0].GetDouble(), (float) rv[1].GetDouble(), 0f)
                        : new Vector3(1280f, 800f, 0f);
                    Vector2 margin = a.TryGetProperty("margin_px", out JsonElement m) && m.ValueKind == JsonValueKind.Array && m.GetArrayLength() >= 2
                        ? new Vector2((float) m[0].GetDouble(), (float) m[1].GetDouble())
                        : new Vector2(40f, 40f);

                    layout.UiAnchors[p.Name] = new UiAnchor
                    {
                        Corner = a.TryGetProperty("corner", out JsonElement c) ? c.GetString() ?? "bottom_left" : "bottom_left",
                        MarginPx = margin,
                        ReferenceHeight = refViewport.Y > 0 ? refViewport.Y : 800f,
                        HeightFrac = Num(a, "height_frac", 0.2f),
                        DepthM = Num(a, "depth_m", 2f),
                    };
                }
            }

            // The option studs: "stud.glb#StudOff@Autologin" and the like,
            // in the order Autologin, Save account, Music (else as written).
            var studs = new List<(int order, Placement p)>();
            int seen = 0;

            foreach (KeyValuePair<string, Placement> kv in layout.Objects)
            {
                if (!kv.Key.StartsWith("stud.glb#", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string inst = kv.Key.Contains('@') ? kv.Key.Substring(kv.Key.IndexOf('@') + 1).ToLowerInvariant() : "";
                int order = inst.StartsWith("auto") ? 0 : inst.StartsWith("save") ? 1 : inst.StartsWith("music") ? 2 : 10 + seen;
                studs.Add((order, kv.Value));
                seen++;
            }

            if (studs.Count > 0)
            {
                studs.Sort((a, b) => a.order.CompareTo(b.order));
                layout.StudSlots = studs.ConvertAll(x => x.p);
            }

            if (root.TryGetProperty("lights", out JsonElement lights) && lights.ValueKind == JsonValueKind.Array)
            {
                var list = new List<LightSpec>();

                foreach (JsonElement l in lights.EnumerateArray())
                {
                    string type = l.TryGetProperty("type", out JsonElement t) ? t.GetString() ?? "omni" : "omni";
                    list.Add(new LightSpec
                    {
                        Type = type,
                        Position = Vec(l, "position", Vector3.Zero),
                        RotationDeg = Vec(l, "rotation_deg", Vector3.Zero),
                        Color = ColorOf(l, "color", Colors.White),
                        Energy = Num(l, "energy", 1f),
                        Range = Num(l, "range", 5f),
                        Flicker = l.TryGetProperty("flicker", out JsonElement f) ? f.ValueKind == JsonValueKind.True : type == "omni",
                    });
                }

                if (list.Count > 0)
                {
                    layout.Lights = list;
                }
            }

            layout.ScrollSlots = Slots(root, "scroll_slots", layout.ScrollSlots);
            layout.PlinthSlots = Slots(root, "plinth_slots", layout.PlinthSlots);
            layout.CardSlots = Slots(root, "card_slots", layout.CardSlots);
            layout.StudSlots = Slots(root, "stud_slots", layout.StudSlots);

            GD.Print($"[GUO] pregame3d: layout from {Path} ({layout.Objects.Count} objects, {layout.Lights.Count} lights)");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[GUO] pregame3d: {Path} unreadable, using the built-in placement: {ex.Message}");
            return new DioramaLayout();
        }

        return layout;
    }

    /// <summary>The file's placement for "file.glb#Node", or for "file.glb", if it has one.</summary>
    public bool TryGet(string file, string node, out Placement p) =>
        Objects.TryGetValue(node == null ? file : $"{file}#{node}", out p) || (node != null && Objects.TryGetValue(node, out p));

    /// <summary>Splits "file.glb#Node@instance" (node and instance may be absent).</summary>
    public static (string file, string node, string instance) ParseKey(string key)
    {
        string file = key, node = null, instance = null;
        int hash = key.IndexOf('#');

        if (hash >= 0)
        {
            file = key.Substring(0, hash);
            node = key.Substring(hash + 1);
            int at = node.IndexOf('@');

            if (at >= 0)
            {
                instance = node.Substring(at + 1);
                node = node.Substring(0, at);
            }
        }

        return (file, node, instance);
    }

    /// <summary>A step's camera: its own from "step_cameras", else null.</summary>
    public (Vector3 position, Vector3 lookAt, float fovDeg)? StepCamera(string step) =>
        StepCameras.TryGetValue(step, out var c) ? c : null;

    private static List<Placement> Slots(JsonElement root, string key, List<Placement> fallback)
    {
        if (!root.TryGetProperty(key, out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return fallback;
        }

        var list = new List<Placement>();

        foreach (JsonElement e in arr.EnumerateArray())
        {
            list.Add(Place(e, default));
        }

        return list.Count > 0 ? list : fallback;
    }

    private static Placement Place(JsonElement e, Placement fallback) => new()
    {
        Position = Vec(e, "position", fallback.Position),
        RotationDeg = Vec(e, "rotation_deg", fallback.RotationDeg),
        Scale = Vec(e, "scale", Vector3.One),
    };

    private static Vector3 Vec(JsonElement e, string key, Vector3 fallback)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out JsonElement v))
        {
            return fallback;
        }

        if (v.ValueKind == JsonValueKind.Number)
        {
            float s = (float) v.GetDouble();
            return new Vector3(s, s, s);
        }

        if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() < 3)
        {
            return fallback;
        }

        return new Vector3((float) v[0].GetDouble(), (float) v[1].GetDouble(), (float) v[2].GetDouble());
    }

    private static float Num(JsonElement e, string key, float fallback) =>
        e.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? (float) v.GetDouble() : fallback;

    private static Color ColorOf(JsonElement e, string key, Color fallback)
    {
        if (!e.TryGetProperty(key, out JsonElement v) || v.ValueKind != JsonValueKind.Array || v.GetArrayLength() < 3)
        {
            return fallback;
        }

        float r = (float) v[0].GetDouble(), g = (float) v[1].GetDouble(), b = (float) v[2].GetDouble();

        // 0..255 written as such.
        if (r > 1f || g > 1f || b > 1f)
        {
            r /= 255f;
            g /= 255f;
            b /= 255f;
        }

        return new Color(r, g, b);
    }
}
