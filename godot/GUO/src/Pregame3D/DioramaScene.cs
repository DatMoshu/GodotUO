// SPDX-License-Identifier: BSD-2-Clause
// GUO addition, not a port.

using System;
using System.Collections.Generic;
using Godot;

namespace GUO.Pregame3D;

/// <summary>
/// The diorama's 3D content: the art's glb models placed by the layout, or a
/// placeholder primitive for anything missing (one log line each), all on the
/// PSX material, plus the camera, the lights and their flicker. Knows nothing
/// of the login; <see cref="PregameDiorama"/> and the stages drive it.
/// </summary>
internal sealed class DioramaScene
{
    public const string AssetDir = "res://assets/pregame3d/";
    private const string PsxShaderPath = AssetDir + "shaders/psx.gdshader";
    private const string ClothShaderPath = AssetDir + "shaders/psx_cloth.gdshader";

    public readonly Node3D Root = new() { Name = "Diorama" };
    public readonly DioramaLayout Layout;
    public Camera3D Camera { get; private set; }

    public Node3D Wall, Cloth, ChestBody, ChestInterior, Plaque, LoginButton, Shield, CreditsPlaque, Torch;

    /// <summary>The lid's hinge: rotate about its local X (see <see cref="SetLidOpen"/>).</summary>
    public Node3D ChestLid;

    /// <summary>The plaque's two fields (children of the plaque or placed on it).</summary>
    public Node3D FieldAccount, FieldPassword;

    public readonly List<Node3D> Candles = new();

    private readonly Dictionary<string, Node> _glbRoots = new();
    private readonly HashSet<string> _told = new();
    private readonly List<ShaderMaterial> _materials = new();
    private readonly List<(OmniLight3D light, float energy, float seed)> _flicker = new();
    private readonly FastNoiseLite _noise = new() { NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin, Frequency = 2.2f };
    private Shader _psx, _cloth;
    private Vector2 _snap = new(640, 400);
    private float _gutter = 1f;

    public DioramaScene()
    {
        Layout = DioramaLayout.Load();
        _psx = Load<Shader>(PsxShaderPath);
        _cloth = Load<Shader>(ClothShaderPath) ?? _psx;
    }

    /// <summary>Nodes instanced per slot, never placed on their own.</summary>
    private static readonly HashSet<string> Templates = new() { "Scroll", "Card", "Plinth", "StudOff", "StudOn" };

    /// <summary>Every node the layout placed, by node name (the first instance of each).</summary>
    private readonly Dictionary<string, Node3D> _placed = new();
    private readonly List<Node3D> _placedAll = new();

    private Placement _lid;

    public void Build()
    {
        Camera = new Camera3D { Name = "Camera", Fov = Layout.FovDeg, Near = 0.05f, Far = 60f, Current = true };
        Root.AddChild(Camera);
        Camera.Transform = new Transform3D(Basis.Identity, Layout.CameraPosition).LookingAt(Layout.CameraLookAt, Vector3.Up);

        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.04f, 0.03f, 0.025f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = Layout.AmbientColor ?? new Color(0.32f, 0.24f, 0.2f),
            AmbientLightEnergy = Layout.AmbientColor.HasValue ? 1f : 0.55f,
            FogEnabled = true,
            FogLightColor = new Color(0.06f, 0.045f, 0.035f),
            FogDensity = 0.06f,
            TonemapMode = Godot.Environment.ToneMapper.Linear,
        };
        Root.AddChild(new WorldEnvironment { Environment = env });

        foreach (LightSpec spec in Layout.Lights)
        {
            AddLight(spec);
        }

        // Everything the layout names, where it says (world transforms).
        foreach (KeyValuePair<string, Placement> kv in Layout.Objects)
        {
            (string file, string node, string instance) = DioramaLayout.ParseKey(kv.Key);

            if (node != null && Templates.Contains(node))
            {
                continue;
            }

            Node3D n = Fetch(file, node);

            if (n == null)
            {
                continue;
            }

            n.Transform = kv.Value.Transform;
            n.Name = (node ?? System.IO.Path.GetFileNameWithoutExtension(file)) + (instance != null ? "_" + instance : "");
            Root.AddChild(n);
            Psx(n, node == "Cloth");
            _placedAll.Add(n);

            if (node != null && !_placed.ContainsKey(node))
            {
                _placed[node] = n;
            }
        }

        Wall = Role("wall.glb", "Wall", PlaceholderWall, new Placement(new Vector3(0f, 1.6f, -1.6f)));
        Cloth = Role("cloth.glb", "Cloth", PlaceholderCloth, new Placement(new Vector3(0f, 0f, 0.3f)), cloth: true);
        ChestBody = Role("chest.glb", "ChestBody", PlaceholderChestBody, new Placement(Vector3.Zero));
        ChestInterior = Role("chest.glb", "ChestInterior", PlaceholderInterior, new Placement(new Vector3(0f, 0.11f, 0f)));
        ChestLid = Role("chest.glb", "ChestLid", PlaceholderLid, new Placement(new Vector3(0f, 0.9f, -0.62f)));

        // The layout gives the lid open (its rotation_deg.x is lid_open_deg);
        // closed is the same with lid_closed_deg.
        _lid = Layout.TryGet("chest.glb", "ChestLid", out Placement lid) ? lid : new Placement(ChestLid.Position, ChestLid.RotationDegrees, ChestLid.Scale);

        Plaque = Role("plaque_login.glb", "LoginPlaque", PlaceholderPlaque, new Placement(new Vector3(0f, 0.62f, -0.12f), new Vector3(53f, 0f, 0f)));
        FieldAccount = Plaque.FindChild("FieldAccount", true, false) as Node3D
            ?? Role("plaque_login.glb", "FieldAccount", PlaceholderField, new Placement(new Vector3(0f, 0.03f, -0.08f)), parent: Plaque);
        FieldPassword = Plaque.FindChild("FieldPassword", true, false) as Node3D
            ?? Role("plaque_login.glb", "FieldPassword", PlaceholderField, new Placement(new Vector3(0f, 0.03f, 0.17f)), parent: Plaque);
        LoginButton = Role("button_login.glb", "LoginButton", PlaceholderLoginButton, new Placement(new Vector3(0f, 0.36f, 0.12f), new Vector3(53f, 0f, 0f)));
        Shield = Role("shield.glb", "Shield", PlaceholderShield, new Placement(new Vector3(-1.55f, 0.62f, 0.25f), new Vector3(-15f, 18f, 0f)));
        CreditsPlaque = Role("plaque_credits.glb", "CreditsPlaque", PlaceholderCredits, new Placement(new Vector3(1.55f, 1.35f, -0.4f), new Vector3(0f, -15f, 0f)));

        foreach (Node3D n in _placedAll)
        {
            if (n.Name.ToString().StartsWith("Candle", StringComparison.Ordinal))
            {
                Candles.Add(n);
            }
        }

        if (Candles.Count == 0)
        {
            foreach (Vector3 at in new[] { new Vector3(-1.25f, 0f, -0.35f), new Vector3(1.25f, 0f, -0.35f) })
            {
                Candles.Add(Place("candle.glb", "Candle", PlaceholderCandle, new Placement(at), keyed: false));
            }
        }

        Torch = _placed.TryGetValue("Torch", out Node3D torch) ? torch : Place("torch.glb", null, PlaceholderTorch, new Placement(new Vector3(-2.1f, 1.7f, -1.5f)));
    }

    /// <summary>A role's node: the one the layout placed, else the glb's or a placeholder (<see cref="Place"/>).</summary>
    private Node3D Role(string file, string node, Func<Node3D> placeholder, Placement fallback, bool cloth = false, Node3D parent = null) =>
        _placed.TryGetValue(node, out Node3D n) ? n : Place(file, node, placeholder, fallback, cloth, parent: parent);

    // --- per-frame ------------------------------------------------------------------

    /// <summary>The internal resolution every PSX material snaps its vertices to.</summary>
    public void SetSnapResolution(Vector2 size)
    {
        _snap = size;

        foreach (ShaderMaterial m in _materials)
        {
            m.SetShaderParameter("snap_resolution", size);
        }
    }

    /// <summary>0..1 of the open angle; 0 is closed (about the hinge's local X, as the art asks).</summary>
    public void SetLidOpen(float t)
    {
        Placement p = _lid;
        p.RotationDeg = new Vector3(Mathf.Lerp(Layout.LidClosedDeg, Layout.LidOpenDeg, t), _lid.RotationDeg.Y, _lid.RotationDeg.Z);
        ChestLid.Transform = p.Transform;
    }

    /// <summary>1 = candles burn normally; lower gutters them (connecting).</summary>
    public void SetGutter(float g) => _gutter = g;

    public void Update(double time)
    {
        foreach ((OmniLight3D light, float energy, float seed) in _flicker)
        {
            float n = _noise.GetNoise2D((float) time * 3.1f, seed * 17f);
            float n2 = _noise.GetNoise2D((float) time * 9.7f, seed * 31f + 5f);
            light.LightEnergy = energy * _gutter * (0.86f + 0.14f * n + 0.06f * n2);
        }
    }

    // --- templates (instanced per slot) ---------------------------------------------

    /// <summary>A fresh copy of a template node (Scroll, Card, Plinth, StudOff, StudOn), PSX'd.</summary>
    public Node3D Instance(string file, string node, Func<Node3D> placeholder)
    {
        Node3D n = Fetch(file, node) ?? placeholder();
        n.Transform = Transform3D.Identity;

        if (Layout.TryGet(file, node, out Placement p))
        {
            // The file's entry for a template gives its scale; the slot places it.
            n.Scale = p.Scale;
        }

        Psx(n, false);

        return n;
    }

    public Node3D Scroll() => Instance("scroll.glb", "Scroll", () => Box(new Vector3(0.62f, 0.07f, 0.16f), Parchment, "Scroll"));

    public Node3D Card()
    {
        Node3D n = Instance("card.glb", "Card", () =>
        {
            Node3D card = Box(new Vector3(0.36f, 0.02f, 0.24f), Parchment, "Card");
            MeshInstance3D seal = Cyl(0.04f, 0.015f, new Color(0.6f, 0.06f, 0.06f));
            seal.Position = new Vector3(0.12f, 0.015f, 0.07f);
            card.AddChild(seal);
            return card;
        });
        return n;
    }

    public Node3D Plinth() => Instance("plinth.glb", "Plinth", () => Box(new Vector3(0.3f, 0.22f, 0.3f), new Color(0.66f, 0.64f, 0.6f), "Plinth", new Vector3(0f, 0.11f, 0f)));

    public Node3D Stud(bool on) => Instance("stud.glb", on ? "StudOn" : "StudOff",
        () => Cyl(0.05f, 0.04f, on ? new Color(0.88f, 0.69f, 0.31f) : new Color(0.24f, 0.23f, 0.25f)));

    // --- loading --------------------------------------------------------------------

    /// <summary>
    /// Puts "file#node" in the world: the art's node at the layout's
    /// placement (or where the glb has it), else the placeholder at
    /// <paramref name="fallback"/>. <paramref name="parent"/> defaults to the root.
    /// </summary>
    private Node3D Place(string file, string node, Func<Node3D> placeholder, Placement fallback, bool cloth = false, bool keyed = true, Node3D parent = null)
    {
        Node3D n = Fetch(file, node);
        bool art = n != null;

        if (!art)
        {
            n = placeholder();
            n.Transform = fallback.Transform;
        }

        if (keyed && Layout.TryGet(file, node, out Placement p))
        {
            n.Transform = p.Transform;
        }
        else if (!keyed)
        {
            n.Transform = art ? new Transform3D(n.Basis, fallback.Position) : fallback.Transform;
        }

        n.Name = node ?? System.IO.Path.GetFileNameWithoutExtension(file);
        (parent ?? Root).AddChild(n);
        Psx(n, cloth);

        return n;
    }

    /// <summary>A copy of <paramref name="node"/> from <paramref name="file"/> with its transform within the glb, or null.</summary>
    private Node3D Fetch(string file, string node)
    {
        Node root = GlbRoot(file);

        if (root == null)
        {
            return null;
        }

        Node3D found = node == null ? root as Node3D : root.FindChild(node, true, false) as Node3D;

        if (found == null)
        {
            Tell($"{file}: no node \"{node}\"; a placeholder stands in");
            return null;
        }

        var copy = (Node3D) found.Duplicate();

        if (node == null)
        {
            copy.Transform = Transform3D.Identity;
            return copy;
        }

        // Its place within the glb: the transforms from the glb's root down.
        Transform3D t = found.Transform;

        for (Node p = found.GetParent(); p != null && p != root; p = p.GetParent())
        {
            if (p is Node3D p3)
            {
                t = p3.Transform * t;
            }
        }

        copy.Transform = t;

        return copy;
    }

    private Node GlbRoot(string file)
    {
        if (_glbRoots.TryGetValue(file, out Node cached))
        {
            return cached;
        }

        string path = AssetDir + file;
        Node root = null;

        try
        {
            if (ResourceLoader.Exists(path))
            {
                root = GD.Load<PackedScene>(path)?.Instantiate();
            }

            // A glb dropped in but not imported yet (a dev run before the
            // editor saw it): read it directly.
            if (root == null && FileAccess.FileExists(path))
            {
                var doc = new GltfDocument();
                var state = new GltfState();

                if (doc.AppendFromFile(ProjectSettings.GlobalizePath(path), state) == Error.Ok)
                {
                    root = doc.GenerateScene(state);
                }
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[GUO] pregame3d: {path}: {ex.Message}");
            root = null;
        }

        if (root == null)
        {
            Tell($"{file} missing; placeholders stand in for it");
        }

        _glbRoots[file] = root;

        return root;
    }

    private void Tell(string what)
    {
        if (_told.Add(what))
        {
            GD.Print("[GUO] pregame3d: " + what);
        }
    }

    private static T Load<T>(string path) where T : Resource
    {
        try
        {
            return ResourceLoader.Exists(path) ? GD.Load<T>(path) : null;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[GUO] pregame3d: {path}: {ex.Message}");
            return null;
        }
    }

    // --- PSX materials --------------------------------------------------------------

    /// <summary>Every mesh surface under <paramref name="root"/> onto its own PSX material, keeping its albedo.</summary>
    public void Psx(Node root, bool cloth)
    {
        if (_psx == null)
        {
            return;
        }

        foreach (MeshInstance3D mi in Hotspot.Meshes(root))
        {
            if (mi.Mesh == null)
            {
                continue;
            }

            bool isCloth = cloth || mi.Name.ToString().StartsWith("Cloth", StringComparison.Ordinal);
            bool isFlame = mi.Name.ToString().StartsWith("Flame", StringComparison.Ordinal);

            for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
            {
                Material src = mi.GetActiveMaterial(s);

                if (src is ShaderMaterial already && _materials.Contains(already))
                {
                    continue;
                }

                var m = new ShaderMaterial { Shader = isCloth ? _cloth : _psx };
                m.SetShaderParameter("snap_resolution", _snap);

                if (src is BaseMaterial3D bm)
                {
                    m.SetShaderParameter("albedo_color", bm.AlbedoColor);

                    if (bm.AlbedoTexture != null)
                    {
                        m.SetShaderParameter("albedo_texture", bm.AlbedoTexture);
                        m.SetShaderParameter("use_texture", true);
                    }

                    // The art bakes shading into COLOR_0 (the cloth's folds): multiply it in when present.
                    m.SetShaderParameter("use_vertex_color", bm.VertexColorUseAsAlbedo || HasColors(mi.Mesh, s));

                    if (bm.Transparency is BaseMaterial3D.TransparencyEnum.AlphaScissor or BaseMaterial3D.TransparencyEnum.Alpha)
                    {
                        m.SetShaderParameter("alpha_cut", bm.Transparency == BaseMaterial3D.TransparencyEnum.AlphaScissor ? bm.AlphaScissorThreshold : 0.5f);
                    }

                    if (bm.EmissionEnabled || isFlame)
                    {
                        m.SetShaderParameter("emission_strength", isFlame ? 1.6f : 0.8f);
                    }
                }
                else if (HasColors(mi.Mesh, s))
                {
                    m.SetShaderParameter("use_vertex_color", true);
                }

                if (isFlame)
                {
                    m.SetShaderParameter("emission_strength", 1.6f);
                }

                mi.SetSurfaceOverrideMaterial(s, m);
                _materials.Add(m);
            }

            mi.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        }
    }

    private static bool HasColors(Mesh mesh, int surface) =>
        mesh is ArrayMesh am && (am.SurfaceGetFormat(surface) & Mesh.ArrayFormat.FormatColor) != 0;

    // --- lights ---------------------------------------------------------------------

    private void AddLight(LightSpec spec)
    {
        Light3D light;

        switch (spec.Type)
        {
            case "directional":
                light = new DirectionalLight3D();
                break;

            case "spot":
                light = new SpotLight3D { SpotRange = spec.Range };
                break;

            default:
                var omni = new OmniLight3D { OmniRange = spec.Range };
                light = omni;

                if (spec.Flicker)
                {
                    _flicker.Add((omni, spec.Energy, _flicker.Count + 1));
                }

                break;
        }

        light.LightColor = spec.Color;
        light.LightEnergy = spec.Energy;
        light.ShadowEnabled = false;
        light.Position = spec.Position;
        light.RotationDegrees = spec.RotationDeg;
        Root.AddChild(light);
    }

    // --- placeholders ---------------------------------------------------------------

    private static readonly Color Wood = new(0.4f, 0.24f, 0.12f);
    private static readonly Color Velvet = new(0.5f, 0.06f, 0.08f);
    private static readonly Color Parchment = new(0.86f, 0.8f, 0.66f);

    private static Texture2D _stone, _velvet, _woodTex;

    private static Texture2D NoiseTexture(Color a, Color b, float frequency, bool stripes)
    {
        var noise = new FastNoiseLite { Frequency = frequency, NoiseType = FastNoiseLite.NoiseTypeEnum.Cellular };
        Image img = Image.CreateEmpty(64, 64, false, Image.Format.Rgb8);

        for (int y = 0; y < 64; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                float n = stripes ? 0.5f + 0.5f * Mathf.Sin(y * 0.9f + noise.GetNoise2D(x * 0.3f, y) * 3f) : 0.5f + 0.5f * noise.GetNoise2D(x, y);
                img.SetPixel(x, y, a.Lerp(b, Mathf.Clamp(n, 0f, 1f)));
            }
        }

        return ImageTexture.CreateFromImage(img);
    }

    private static StandardMaterial3D Mat(Color c, Texture2D tex = null) => new() { AlbedoColor = c, AlbedoTexture = tex };

    public static Node3D Box(Vector3 size, Color c, string name, Vector3 offset = default, Texture2D tex = null)
    {
        var holder = new Node3D { Name = name };
        var mi = new MeshInstance3D { Name = name + "Mesh", Mesh = new BoxMesh { Size = size, Material = Mat(c, tex) }, Position = offset };
        holder.AddChild(mi);
        return holder;
    }

    private static MeshInstance3D Cyl(float radius, float height, Color c) =>
        new() { Mesh = new CylinderMesh { TopRadius = radius, BottomRadius = radius, Height = height, RadialSegments = 10, Rings = 1, Material = Mat(c) } };

    private static Node3D PlaceholderWall()
    {
        _stone ??= NoiseTexture(new Color(0.2f, 0.19f, 0.18f), new Color(0.42f, 0.4f, 0.37f), 0.09f, false);
        var n = new Node3D();
        n.AddChild(new MeshInstance3D { Name = "WallMesh", Mesh = new QuadMesh { Size = new Vector2(9f, 5f), Material = Mat(Colors.White, _stone) } });
        return n;
    }

    private static Node3D PlaceholderCloth()
    {
        _velvet ??= NoiseTexture(Velvet.Darkened(0.4f), Velvet.Lightened(0.15f), 0.12f, false);
        var n = new Node3D();
        n.AddChild(new MeshInstance3D
        {
            Name = "ClothMesh",
            Mesh = new PlaneMesh { Size = new Vector2(5.2f, 3.6f), SubdivideWidth = 14, SubdivideDepth = 10, Material = Mat(Colors.White, _velvet) },
        });
        return n;
    }

    private static Node3D PlaceholderChestBody()
    {
        _woodTex ??= NoiseTexture(Wood.Darkened(0.35f), Wood.Lightened(0.15f), 0.2f, true);
        var n = new Node3D();
        // Hollow: a floor and four walls, 2.0 x 0.9 x 1.24.
        (Vector3 size, Vector3 at)[] parts =
        {
            (new Vector3(2f, 0.1f, 1.24f), new Vector3(0f, 0.05f, 0f)),
            (new Vector3(2f, 0.9f, 0.08f), new Vector3(0f, 0.45f, 0.58f)),
            (new Vector3(2f, 0.9f, 0.08f), new Vector3(0f, 0.45f, -0.58f)),
            (new Vector3(0.08f, 0.9f, 1.24f), new Vector3(-0.96f, 0.45f, 0f)),
            (new Vector3(0.08f, 0.9f, 1.24f), new Vector3(0.96f, 0.45f, 0f)),
        };

        foreach ((Vector3 size, Vector3 at) in parts)
        {
            n.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = size, Material = Mat(Colors.White, _woodTex) }, Position = at });
        }

        // Iron bands on the front.
        foreach (float x in new[] { -0.8f, 0.8f })
        {
            n.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.1f, 0.92f, 0.02f), Material = Mat(new Color(0.3f, 0.28f, 0.26f)) }, Position = new Vector3(x, 0.45f, 0.625f) });
        }

        return n;
    }

    private static Node3D PlaceholderInterior()
    {
        _velvet ??= NoiseTexture(Velvet.Darkened(0.4f), Velvet.Lightened(0.15f), 0.12f, false);
        var n = new Node3D();
        n.AddChild(new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(1.84f, 1.08f), Material = Mat(new Color(1f, 0.85f, 0.85f), _velvet) } });
        return n;
    }

    private static Node3D PlaceholderLid()
    {
        _woodTex ??= NoiseTexture(Wood.Darkened(0.35f), Wood.Lightened(0.15f), 0.2f, true);
        // The hinge is this node's origin, at the back top edge; the lid runs forward (+Z).
        var n = new Node3D();
        n.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(2.04f, 0.14f, 1.28f), Material = Mat(Colors.White, _woodTex) }, Position = new Vector3(0f, 0.07f, 0.62f) });
        // Velvet lining on its underside.
        n.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(1.8f, 0.01f, 1.05f), Material = Mat(Velvet) }, Position = new Vector3(0f, -0.005f, 0.62f) });
        return n;
    }

    private static Node3D PlaceholderPlaque()
    {
        // An oval stone tablet: a flattened cylinder whose axis (+Y) is its face normal.
        var n = new Node3D();
        n.AddChild(new MeshInstance3D
        {
            Name = "PlaqueMesh",
            Mesh = new CylinderMesh { TopRadius = 0.5f, BottomRadius = 0.5f, Height = 0.05f, RadialSegments = 18, Rings = 1, Material = Mat(new Color(0.46f, 0.42f, 0.4f)) },
            Scale = new Vector3(1.4f, 1f, 0.95f),
        });
        return n;
    }

    private static Node3D PlaceholderField() => Box(new Vector3(0.8f, 0.012f, 0.13f), Parchment, "Field");

    private static Node3D PlaceholderLoginButton() => Box(new Vector3(0.4f, 0.05f, 0.13f), new Color(0.72f, 0.5f, 0.2f), "LoginButton");

    private static Node3D PlaceholderShield()
    {
        var n = new Node3D();
        n.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.5f, 0.6f, 0.06f), Material = Mat(new Color(0.14f, 0.42f, 0.16f)) } });
        n.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.54f, 0.06f, 0.07f), Material = Mat(new Color(0.75f, 0.6f, 0.3f)) }, Position = new Vector3(0f, 0.28f, 0f) });
        return n;
    }

    private static Node3D PlaceholderCredits() => Box(new Vector3(0.62f, 0.2f, 0.05f), new Color(0.2f, 0.34f, 0.52f), "CreditsPlaque");

    private static Node3D PlaceholderCandle()
    {
        var n = new Node3D();
        MeshInstance3D wax = Cyl(0.06f, 0.4f, new Color(0.9f, 0.86f, 0.72f));
        wax.Position = new Vector3(0f, 0.2f, 0f);
        n.AddChild(wax);
        var flame = new MeshInstance3D { Name = "Flame", Mesh = new SphereMesh { Radius = 0.035f, Height = 0.1f, RadialSegments = 6, Rings = 3, Material = Mat(new Color(1f, 0.75f, 0.3f)) }, Position = new Vector3(0f, 0.45f, 0f) };
        n.AddChild(flame);
        return n;
    }

    private static Node3D PlaceholderTorch()
    {
        var n = new Node3D();
        n.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.07f, 0.5f, 0.07f), Material = Mat(Wood) }, RotationDegrees = new Vector3(20f, 0f, 0f) });
        n.AddChild(new MeshInstance3D { Name = "Flame", Mesh = new SphereMesh { Radius = 0.08f, Height = 0.22f, RadialSegments = 6, Rings = 3, Material = Mat(new Color(1f, 0.6f, 0.2f)) }, Position = new Vector3(0f, 0.32f, 0.1f) });
        return n;
    }
}
