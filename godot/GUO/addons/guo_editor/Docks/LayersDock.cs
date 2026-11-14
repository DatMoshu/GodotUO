#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using GUO.Renderer;

/// <summary>
/// The Layers dock: the terrain underlays/overlays in layers.json, as a
/// list the user can add to and remove from. Each layer is a PNG registered
/// to a tile rect; the game reads the manifest at boot, so the dock says so
/// and every mutation saves immediately. Images picked outside the layer
/// folder are copied into overlays/ (or underlays/) so the manifest stays
/// portable. Manual authoring stays available with AI disabled; optional
/// generation uses the shared editor AI gate.
/// </summary>
[Tool]
public partial class LayersDock : EditorDock
{
    private WorldView _world;
    private ItemList _list;
    private TextureRect _preview;
    private LineEdit _id, _name, _image, _prompt;
    private OptionButton _kind;
    private SpinBox _facet, _x0, _y0, _x1, _y1, _opacity;
    private Label _dir, _status, _promptLabel;
    private Button _add, _update, _remove;
    private bool _ready;

    private List<TerrainLayer> _layers = new();
    private LayerDefaults _defaults = new();

    /// <summary>Layer ids on show, for the smoke.</summary>
    public IReadOnlyList<string> LayerIds => _layers.Select(l => l.Id).ToList();

    public string Status => _status?.Text ?? "";

    internal void Attach(WorldView world)
    {
        _world = world;
    }

    /// <summary>The selected layer's preview texture, for the smoke.</summary>
    public Texture2D Preview => _preview?.Texture as Texture2D;

    /// <summary>Fills the form programmatically (the smoke drives the same fields a user types in).</summary>
    public void SetForm(string id, string name, bool underlay, int facet,
        int x0, int y0, int x1, int y1, string image, string prompt, float opacity)
    {
        _id.Text = id;
        _name.Text = name;
        _kind.Selected = underlay ? 1 : 0;
        _facet.Value = facet;
        _x0.Value = x0;
        _y0.Value = y0;
        _x1.Value = x1;
        _y1.Value = y1;
        _image.Text = image;
        _prompt.Text = prompt;
        _opacity.Value = opacity;
    }

    public LayersDock()
    {
        Name = "UOLayers";
        Title = "Layers";
        LayoutKey = "guo_layers";
        DefaultSlot = DockSlot.Bottom;
        AvailableLayouts = DockLayout.Horizontal | DockLayout.Vertical | DockLayout.Floating;
        IconName = "Image";
    }

    public override void _Ready()
    {
        if (_ready)
        {
            return;
        }

        _ready = true;
        var root = new VBoxContainer();
        root.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(root);

        var top = new HBoxContainer();
        root.AddChild(top);
        _dir = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true };
        top.AddChild(_dir);
        var refresh = new Button { Text = "Refresh" };
        refresh.Pressed += () => Refresh();
        top.AddChild(refresh);
        var folder = new Button { Text = "Open folder" };
        folder.Pressed += () => OS.ShellOpen(TerrainLayers.LayerDir());
        top.AddChild(folder);

        var mid = new HBoxContainer();
        mid.SizeFlagsVertical = SizeFlags.ExpandFill;
        root.AddChild(mid);
        _list = new ItemList
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(220, 120),
        };
        _list.ItemSelected += _ => FillFromSelected();
        mid.AddChild(_list);
        _preview = new TextureRect
        {
            CustomMinimumSize = new Vector2(160, 120),
            SizeFlagsVertical = SizeFlags.ExpandFill,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            // Never filter pixel art.
            TextureFilter = TextureFilterEnum.Nearest,
        };
        mid.AddChild(_preview);

        // Bottom half, split: the layer form on the left, the three work
        // previews (extracted, generated, mask) stacked on the right.
        var split = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        root.AddChild(split);
        var left = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsStretchRatio = 1f };
        split.AddChild(left);
        var right = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 1f,
            CustomMinimumSize = new Vector2(300, 0),
        };
        split.AddChild(right);

        var form = new GridContainer { Columns = 4 };
        left.AddChild(form);
        _id = Field(form, "Id");
        _name = Field(form, "Name");
        _kind = new OptionButton();
        _kind.AddItem("Overlay");
        _kind.AddItem("Underlay");
        form.AddChild(new Label { Text = "Kind" });
        form.AddChild(_kind);
        _facet = Spin(form, "Facet", 0, 255, 1);
        _x0 = Spin(form, "X0", 0, 16384, 1);
        _y0 = Spin(form, "Y0", 0, 16384, 1);
        _x1 = Spin(form, "X1", 0, 16384, 1);
        _y1 = Spin(form, "Y1", 0, 16384, 1);
        _opacity = Spin(form, "Opacity", 0, 1, 0.05);
        _opacity.Value = 1;

        var imgRow = new HBoxContainer();
        left.AddChild(imgRow);
        imgRow.AddChild(new Label { Text = "Image" });
        _image = new LineEdit { SizeFlagsHorizontal = SizeFlags.ExpandFill, PlaceholderText = "overlays/name.png" };
        imgRow.AddChild(_image);
        var browse = new Button { Text = "Browse" };
        browse.Pressed += BrowseImage;
        imgRow.AddChild(browse);

        var promptRow = new HBoxContainer();
        left.AddChild(promptRow);
        _promptLabel = new Label { Text = "Layer note" };
        promptRow.AddChild(_promptLabel);
        _prompt = new LineEdit { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        promptRow.AddChild(_prompt);

        var actions = new HBoxContainer();
        left.AddChild(actions);
        _add = new Button { Text = "Add" };
        _add.Pressed += () => Report(AddFromUi());
        actions.AddChild(_add);
        _update = new Button { Text = "Update" };
        _update.Pressed += () => Report(UpdateSelected());
        actions.AddChild(_update);
        _remove = new Button { Text = "Remove" };
        _remove.Pressed += () => Report(RemoveSelected());
        actions.AddChild(_remove);
        var reloadWorld = new Button { Text = "Reload world" };
        reloadWorld.Pressed += () => Report(ReloadWorldView());
        actions.AddChild(reloadWorld);

        left.AddChild(new Label
        {
            Text = "The game reads layers.json at boot: -relayers in the client, Reload world for the World tab above.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        });
        BuildAuthorUi(left, right);
        _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        root.AddChild(_status);
        Refresh();
        ApplyAiFeatures();
    }

    private static LineEdit Field(GridContainer form, string label)
    {
        form.AddChild(new Label { Text = label });
        var edit = new LineEdit { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        form.AddChild(edit);
        return edit;
    }

    private static SpinBox Spin(GridContainer form, string label, double min, double max, double step)
    {
        form.AddChild(new Label { Text = label });
        var spin = new SpinBox { MinValue = min, MaxValue = max, Step = step, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        form.AddChild(spin);
        return spin;
    }

    private void Report(string text)
    {
        if (_status != null)
        {
            _status.Text = text ?? "";
        }

        if (text != null)
        {
            GD.Print($"[GUO editor] layers: {text}");
        }
    }

    /// <summary>Reloads the manifest from disk and shows it. Keeps the selection by id.</summary>
    public void Refresh()
    {
        string keep = SelectedId();
        _layers = TerrainLayers.Load(out _defaults);
        _dir.Text = TerrainLayers.LayerDir();
        _list.Clear();
        foreach (TerrainLayer l in _layers)
        {
            int at = _list.AddItem($"{l.Id} — {(l.Kind == LayerKind.Underlay ? "under" : "over")} f{l.Facet} ({l.X0},{l.Y0})-({l.X1},{l.Y1})");
            _list.SetItemMetadata(at, l.Id);
            if (l.Id == keep)
            {
                _list.Select(at);
            }
        }

        if (_list.IsAnythingSelected())
        {
            FillFromSelected();
        }
        else
        {
            ClearFields();
        }

        UpdateButtons();
    }

    private string SelectedId()
    {
        var items = _list?.GetSelectedItems();
        if (items == null || items.Length == 0)
        {
            return null;
        }

        return _list.GetItemMetadata(items[0]).AsString();
    }

    private TerrainLayer Selected()
    {
        string id = SelectedId();
        return id == null ? null : _layers.FirstOrDefault(l => l.Id == id);
    }

    /// <summary>Selects a layer by id, filling the form (used after add, and by the smoke).</summary>
    public void Select(string id)
    {
        for (int i = 0; i < _list.ItemCount; i++)
        {
            if (_list.GetItemMetadata(i).AsString() == id)
            {
                _list.Select(i);
                FillFromSelected();
                return;
            }
        }
    }

    private void ClearFields()
    {
        _id.Text = "";
        _name.Text = "";
        _kind.Selected = 0;
        _facet.Value = 0;
        _x0.Value = 0;
        _y0.Value = 0;
        _x1.Value = 0;
        _y1.Value = 0;
        _opacity.Value = 1;
        _image.Text = "";
        _prompt.Text = "";
        _preview.Texture = null;
    }

    private void FillFromSelected()
    {
        TerrainLayer l = Selected();
        if (l == null)
        {
            ClearFields();
            UpdateButtons();
            return;
        }

        _id.Text = l.Id;
        _name.Text = l.Name;
        _kind.Selected = l.Kind == LayerKind.Underlay ? 1 : 0;
        _facet.Value = l.Facet;
        _x0.Value = Math.Min(l.X0, l.X1);
        _y0.Value = Math.Min(l.Y0, l.Y1);
        _x1.Value = Math.Max(l.X0, l.X1);
        _y1.Value = Math.Max(l.Y0, l.Y1);
        _opacity.Value = Math.Max(0, Math.Min(1, l.Opacity));
        _image.Text = l.Image;
        _prompt.Text = l.Prompt ?? "";
        _preview.Texture = TerrainLayers.LoadTexture(TerrainLayers.LayerDir(), l);
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool has = Selected() != null;
        _update.Disabled = !has;
        _remove.Disabled = !has;
    }

    private void BrowseImage()
    {
        FileDialog dialog = null;
        try
        {
            string dir = TerrainLayers.LayerDir();
            string sub = _kind.Selected == 1 ? "underlays" : "overlays";
            string browseDir = TerrainLayerPaths.CreateDirectory(dir, sub);
            dialog = new FileDialog
            {
                Access = FileDialog.AccessEnum.Filesystem,
                FileMode = FileDialog.FileModeEnum.OpenFile,
                CurrentDir = browseDir,
            };
            dialog.AddFilter("*.png", "PNG images");
            AddChild(dialog);
            FileDialog picker = dialog;
            dialog.FileSelected += path =>
            {
                try
                {
                    _image.Text = StageImage(path, _kind.Selected == 1);
                }
                catch (Exception ex)
                {
                    Report($"Could not import image: {ex.Message}");
                }
                finally
                {
                    picker.QueueFree();
                }
            };
            dialog.Canceled += dialog.QueueFree;
            dialog.PopupCentered(new Vector2I(700, 450));
        }
        catch (Exception ex)
        {
            dialog?.QueueFree();
            Report($"Could not browse images: {ex.Message}");
        }
    }

    /// <summary>
    /// Safe inside files stay in place; an explicitly picked outside source
    /// (including a redirected/network selection) is copied byte-for-byte.
    /// Only its destination is an untrusted layer-relative reference.
    /// </summary>
    public static string StageImage(string path, bool underlay)
    {
        string dir = TerrainLayers.LayerDir();
        string full = Path.GetFullPath(path);
        if (TerrainLayerPaths.IsInside(dir, full, out string relative))
        {
            // A rejected inside descendant must never fall back to outside import.
            string inside = TerrainLayerPaths.Resolve(dir, relative, out relative);
            if (!File.Exists(inside))
                throw new FileNotFoundException("Selected layer image was not found.");
            return relative;
        }

        string sub = underlay ? "underlays" : "overlays";
        string dest = TerrainLayerPaths.Resolve(dir, $"{sub}/{Path.GetFileName(full)}", out relative);
        TerrainLayerPaths.CreateDirectory(dir, sub);
        dest = TerrainLayerPaths.Resolve(dir, relative, out relative);
        File.Copy(full, dest, true);
        GD.Print($"[GUO editor] layers: copied {full} to {dest}");
        return relative;
    }

    private TerrainLayer ReadUi(string id)
    {
        return new TerrainLayer
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(_name.Text) ? id : _name.Text.Trim(),
            Kind = _kind.Selected == 1 ? LayerKind.Underlay : LayerKind.Overlay,
            Facet = (int)_facet.Value,
            X0 = (int)_x0.Value,
            Y0 = (int)_y0.Value,
            X1 = (int)_x1.Value,
            Y1 = (int)_y1.Value,
            Image = _image.Text ?? "",
            Prompt = _prompt.Text ?? "",
            Opacity = (float)_opacity.Value,
        };
    }

    private bool ImageResolves(TerrainLayer l)
    {
        if (!TerrainLayerPaths.TryResolve(TerrainLayers.LayerDir(), l.Image,
            out string path, out string relative, out _) || !File.Exists(path))
            return false;
        l.Image = relative;
        return true;
    }

    /// <summary>Adds the form as a new layer and saves. Empty id becomes layer_N.</summary>
    public string AddFromUi()
    {
        string id = (_id.Text ?? "").Trim();
        if (id.Length == 0)
        {
            int n = _layers.Count + 1;
            while (_layers.Any(l => l.Id == $"layer_{n}"))
            {
                n++;
            }

            id = $"layer_{n}";
        }

        if (_layers.Any(l => string.Equals(l.Id, id, StringComparison.OrdinalIgnoreCase)))
        {
            return $"There is already a layer {id}.";
        }

        TerrainLayer l = ReadUi(id);
        if (!ImageResolves(l))
        {
            return $"Image not found under {TerrainLayers.LayerDir()}: {l.Image}.";
        }

        var proposed = new List<TerrainLayer>(_layers) { l };
        bool ok = Persist(proposed);
        if (ok)
        {
            Select(id);
        }

        return ok ? $"Added {id}. -relayers in the client to see it." : "Could not write layers.json.";
    }

    /// <summary>Writes the form back to the selected layer and saves.</summary>
    public string UpdateSelected()
    {
        TerrainLayer l = Selected();
        if (l == null)
        {
            return "Select a layer first.";
        }

        TerrainLayer next = ReadUi(l.Id);
        if (!ImageResolves(next))
        {
            return $"Image not found under {TerrainLayers.LayerDir()}: {next.Image}.";
        }

        var proposed = new List<TerrainLayer>(_layers);
        proposed[proposed.IndexOf(l)] = next;
        return Persist(proposed) ? $"Updated {l.Id}. -relayers in the client to see it." : "Could not write layers.json.";
    }

    /// <summary>Removes the selected layer (the PNG stays) and saves.</summary>
    public string RemoveSelected()
    {
        TerrainLayer l = Selected();
        if (l == null)
        {
            return "Select a layer first.";
        }

        var proposed = new List<TerrainLayer>(_layers);
        proposed.Remove(l);
        return Persist(proposed) ? $"Removed {l.Id}." : "Could not write layers.json.";
    }

    private bool Persist(List<TerrainLayer> proposed)
    {
        if (!TerrainLayers.Save(proposed, _defaults))
            return false;
        _layers = proposed;
        Refresh();
        return true;
    }

    /// <summary>
    /// Re-reads layers.json into the World tab's own scene (same call the
    /// client's -relayers makes). Needs the World tab to have booted once;
    /// it starts the first time it is shown, not with the editor.
    /// </summary>
    public string ReloadWorldView()
    {
        var scene = _world?.Host?.Scene;
        if (scene == null)
        {
            return "World tab has no scene yet: show it once so it boots, then reload.";
        }

        scene.ReloadTerrainLayers();
        return "World view reloaded.";
    }
}
#endif
