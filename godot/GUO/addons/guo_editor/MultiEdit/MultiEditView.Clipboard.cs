#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Godot;
using GUO.Assets;

/// <summary>
/// Copy, cut and paste with a ghost, the stamps library, and erasing a whole stair or roof (ADR-0031, phase 2).
/// A paste or a stamp is a ghost that follows the pointer; a click places it as one undo step. Stamps are
/// components descriptions (data_formats section 28) under the user's GUO folder (<c>user://</c>), so they carry
/// hue and survive a rebuilt editor.
/// </summary>
public partial class MultiEditView
{
    private static List<MultiPart> _clip = new();
    private ItemList _stampList;
    private LineEdit _stampName;
    private Label _stampLog;

    /// <summary>Where stamps live; the smoke check points this at its own folder.</summary>
    public string StampsDir { get; set; } = ProjectSettings.GlobalizePath("user://guo_multiedit_stamps");

    public int ClipboardCount => _clip.Count;

    private void BuildClipboard()
    {
        _canvas.Command += RunCommand;
        _canvas.GhostPlaced += OnGhostPlaced;
        _canvas.GhostCancelled += () => _status.Text = "paste cancelled";
        _canvas.EraseGroupRequested += uid => EraseGroup(uid);

        var box = new VBoxContainer { Name = "Stamps" };
        box.AddChild(new Label { Text = "Save the selection as a stamp, then place it with a ghost.", AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(250, 0) });
        _stampName = new LineEdit { PlaceholderText = "stamp name" };
        box.AddChild(_stampName);
        box.AddChild(Btn("Save selection as stamp", () => SaveStamp(_stampName.Text)));
        _stampList = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 120), TextureFilter = TextureFilterEnum.Nearest };
        _stampList.ItemActivated += i => PlaceStamp(_stampList.GetItemText((int)i));
        box.AddChild(_stampList);
        box.AddChild(Btn("Place with ghost", () =>
        {
            int[] sel = _stampList.GetSelectedItems();
            if (sel.Length > 0)
            {
                PlaceStamp(_stampList.GetItemText(sel[0]));
            }
        }));
        box.AddChild(Btn("Delete stamp", () =>
        {
            int[] sel = _stampList.GetSelectedItems();
            if (sel.Length > 0)
            {
                DeleteStamp(_stampList.GetItemText(sel[0]));
            }
        }));
        _stampLog = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(250, 0) };
        box.AddChild(_stampLog);
        AddSidePanel("Stamps", box);
        RefreshStamps();
    }

    private void BuildClipboardButtons(Control row)
    {
        row.AddChild(new VSeparator());
        row.AddChild(Tip(Btn("Copy", () => CopySelection()), "Ctrl+C"));
        row.AddChild(Tip(Btn("Cut", () => CutSelection()), "Ctrl+X"));
        row.AddChild(Tip(Btn("Paste", () => PasteClipboard()), "Ctrl+V: a ghost follows the pointer, click to place, Esc to drop"));
        row.AddChild(Tip(Btn("Erase group", () => EraseGroupOfSelection()), "Erase the whole stair or roof the selection touches (Alt+click with the Erase tool)"));
    }

    private void RunCommand(string name)
    {
        switch (name)
        {
            case "copy": CopySelection(); break;
            case "cut": CutSelection(); break;
            case "paste": PasteClipboard(); break;
        }
    }

    private List<MultiPart> SelectedParts() => _doc.Parts.Where(p => _doc.Selection.Contains(p.Uid)).ToList();

    /// <summary>The parts re-based on the centre of their box, so a paste or stamp lands under the pointer.</summary>
    private static List<MultiPart> Centred(List<MultiPart> parts)
    {
        int cx = (parts.Min(p => (int)p.X) + parts.Max(p => (int)p.X)) / 2, cy = (parts.Min(p => (int)p.Y) + parts.Max(p => (int)p.Y)) / 2;
        return parts.Select(p => { MultiPart q = p; q.X = (short)(p.X - cx); q.Y = (short)(p.Y - cy); q.Uid = 0; return q; }).ToList();
    }

    public bool CopySelection()
    {
        List<MultiPart> sel = SelectedParts();
        if (sel.Count == 0)
        {
            _status.Text = "select something to copy";
            return false;
        }

        _clip = Centred(sel);
        _status.Text = $"copied {_clip.Count} component(s)";
        return true;
    }

    public bool CutSelection()
    {
        if (!CopySelection())
        {
            return false;
        }

        _doc.Remove(_doc.Selection.ToList(), $"cut {_clip.Count} component(s)");
        return true;
    }

    /// <summary>Starts a paste: the clipboard becomes a ghost that follows the pointer.</summary>
    public bool PasteClipboard()
    {
        if (_clip.Count == 0)
        {
            _status.Text = "nothing copied";
            return false;
        }

        BeginGhost(_clip, "paste");
        return true;
    }

    private string _ghostWhat = "paste";

    private void BeginGhost(List<MultiPart> parts, string what)
    {
        _ghostWhat = what;
        _ghostIsGen = false;
        _canvas.SetGhost(parts, follow: true);
        _status.Text = $"{what}: move the pointer, click to place, Esc to cancel";
    }

    private void OnGhostPlaced(int dx, int dy, int dz)
    {
        List<MultiPart> ghost = _canvas.GhostParts.ToList();
        if (ghost.Count == 0)
        {
            return;
        }

        var made = ghost.Select(p => _doc.Make(p.Id, p.X + dx, p.Y + dy, p.Z + dz, p.Shown, p.Hue)).ToList();
        _doc.Do($"{_ghostWhat} {made.Count}", list => list.AddRange(made));
        _doc.Selection.Clear();
        foreach (MultiPart p in made)
        {
            _doc.Selection.Add(p.Uid);
        }

        _canvas.ClearGhost();
        UpdateSelectionUi();
    }

    // --- stamps -----------------------------------------------------------------------------------------

    public IReadOnlyList<string> Stamps() =>
        Directory.Exists(StampsDir)
            ? Directory.GetFiles(StampsDir, "*.multi.json").Select(f => Path.GetFileName(f)[..^".multi.json".Length]).OrderBy(n => n).ToList()
            : new List<string>();

    private void RefreshStamps()
    {
        if (_stampList == null)
        {
            return;
        }

        _stampList.Clear();
        foreach (string s in Stamps())
        {
            _stampList.AddItem(s);
        }
    }

    public string SaveStamp(string name)
    {
        List<MultiPart> sel = SelectedParts();
        name = MultiStore.SafeName(name);
        if (sel.Count == 0 || string.IsNullOrWhiteSpace(name))
        {
            _stampLog.Text = "select parts and name the stamp";
            return null;
        }

        Directory.CreateDirectory(StampsDir);
        string path = Path.Combine(StampsDir, name + ".multi.json");
        File.WriteAllText(path, MultiStore.ToJson(name, null, Centred(sel)), new UTF8Encoding(false));
        _stampLog.Text = $"stamp {name}: {sel.Count} components";
        RefreshStamps();
        return path;
    }

    public List<MultiPart> ReadStamp(string name)
    {
        string path = Path.Combine(StampsDir, MultiStore.SafeName(name) + ".multi.json");
        return File.Exists(path) ? MultiStore.FromJson(File.ReadAllText(path)).Parts : null;
    }

    public bool PlaceStamp(string name)
    {
        List<MultiPart> parts = ReadStamp(name);
        if (parts == null || parts.Count == 0)
        {
            _stampLog.Text = $"no stamp {name}";
            return false;
        }

        BeginGhost(parts, $"stamp {name}");
        return true;
    }

    public bool DeleteStamp(string name)
    {
        string path = Path.Combine(StampsDir, MultiStore.SafeName(name) + ".multi.json");
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        RefreshStamps();
        return true;
    }

    // --- erasing a whole stair or roof --------------------------------------------------------------------

    private string KindOf(MultiPart p)
    {
        StaticTiles[] tiles = _data.Files.TileData.StaticData;
        StaticTiles t = p.Id < tiles.Length ? tiles[p.Id] : default;
        if ((_tables?.RoofIds.Contains(p.Id) ?? false) || t.IsRoof)
        {
            return "roof";
        }

        if ((_tables?.StairIds.Contains(p.Id) ?? false) || (t.Flags & (TileFlag.StairBack | TileFlag.StairRight)) != 0)
        {
            return "stair";
        }

        return null;
    }

    /// <summary>
    /// The whole stair or roof a part belongs to: the parts of its kind that touch it (a cell apart, any height), and for
    /// a stair the solid blocks under its steps. Empty when the part is neither.
    /// </summary>
    public List<int> GroupOf(int uid)
    {
        MultiPart seed = _doc.Parts.FirstOrDefault(p => p.Uid == uid);
        string kind = seed.Uid == uid ? KindOf(seed) : null;
        if (kind == null)
        {
            return new List<int>();
        }

        List<MultiPart> same = _doc.Parts.Where(p => KindOf(p) == kind).ToList();
        var group = new HashSet<int> { uid };
        var queue = new Queue<MultiPart>();
        queue.Enqueue(seed);
        while (queue.Count > 0)
        {
            MultiPart a = queue.Dequeue();
            foreach (MultiPart b in same)
            {
                if (!group.Contains(b.Uid) && Math.Abs(a.X - b.X) <= 1 && Math.Abs(a.Y - b.Y) <= 1)
                {
                    group.Add(b.Uid);
                    queue.Enqueue(b);
                }
            }
        }

        if (kind == "stair")
        {
            var top = _doc.Parts.Where(p => group.Contains(p.Uid)).GroupBy(p => (p.X, p.Y)).ToDictionary(g => g.Key, g => g.Max(p => p.Z));
            foreach (MultiPart p in _doc.Parts)
            {
                if (!group.Contains(p.Uid) && top.TryGetValue((p.X, p.Y), out short z) && p.Z < z && !_canvas.IsFloorPart(p) && KindOf(p) == null
                    && (_data.Files.TileData.StaticData[p.Id].Flags & TileFlag.Wall) == 0)
                {
                    group.Add(p.Uid);
                }
            }
        }

        return group.ToList();
    }

    public bool EraseGroup(int uid)
    {
        List<int> g = GroupOf(uid);
        if (g.Count == 0)
        {
            _status.Text = "that is not a stair or a roof";
            return false;
        }

        MultiPart seed = _doc.Parts.First(p => p.Uid == uid);
        return _doc.Remove(g, $"erase {KindOf(seed)} ({g.Count})");
    }

    public bool EraseGroupOfSelection()
    {
        var all = new HashSet<int>();
        foreach (int uid in _doc.Selection.ToList())
        {
            foreach (int g in GroupOf(uid))
            {
                all.Add(g);
            }
        }

        if (all.Count == 0)
        {
            _status.Text = "select a stair or roof piece first";
            return false;
        }

        return _doc.Remove(all, $"erase group ({all.Count})");
    }
}
#endif
