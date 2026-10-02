#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

/// <summary>Finding things in the editor's own node tree, which has no API for most of what F3 wants.</summary>
internal static class GodotUi
{
    public static Control Base => EditorInterface.Singleton.GetBaseControl();

    public static IEnumerable<Node> Walk(Node root)
    {
        foreach (Node c in root.GetChildren())
        {
            yield return c;
            foreach (Node d in Walk(c))
            {
                yield return d;
            }
        }
    }

    public static IEnumerable<T> All<T>(Node root) where T : Node => Walk(root).OfType<T>();

    /// <summary>Calls <paramref name="action"/> after a number of frames, on the main thread.</summary>
    public static async void AfterFrames(int frames, Action action)
    {
        SceneTree tree = Base.GetTree();
        for (int i = 0; i < frames; i++)
        {
            await Base.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        }

        try
        {
            action();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[GUO editor] F3 follow-up failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public static string Pretty(string segment) =>
        string.Join(' ', segment.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..]));

    /// <summary>
    /// Runs a menu item the way a click does: the editor wires every menu to
    /// its <c>id_pressed</c> signal.
    /// </summary>
    public static void Press(PopupMenu menu, int id)
    {
        if (menu == null || !GodotObject.IsInstanceValid(menu))
        {
            return;
        }

        menu.EmitSignal(PopupMenu.SignalName.IdPressed, id);
    }
}

/// <summary>
/// Every item of the editor's menu bar (Scene, Project, Debug, Editor, Help),
/// submenus included, run by emitting the item's id_pressed. Rebuilt each
/// time F3 opens, because some of it changes (recent scenes, layouts).
/// </summary>
public sealed class GodotMenuProvider : SearchProvider
{
    private readonly List<SearchEntry> _entries = new();
    private readonly Dictionary<string, (PopupMenu Menu, int Id)> _byTitle = new(StringComparer.OrdinalIgnoreCase);

    public override string Name => "menus";

    public override IEnumerable<SearchEntry> Entries => _entries;

    public override void Refresh()
    {
        _entries.Clear();
        _byTitle.Clear();
        foreach (MenuBar bar in GodotUi.All<MenuBar>(GodotUi.Base).ToList())
        {
            foreach (PopupMenu menu in bar.GetChildren().OfType<PopupMenu>())
            {
                Collect(menu, menu.Name.ToString(), 0);
            }
        }
    }

    private void Collect(PopupMenu menu, string path, int depth)
    {
        if (depth > 4)
        {
            return;
        }

        for (int i = 0; i < menu.ItemCount; i++)
        {
            if (menu.IsItemSeparator(i))
            {
                continue;
            }

            string text = menu.GetItemText(i).Trim();
            if (text.Length == 0)
            {
                continue;
            }

            PopupMenu sub = menu.GetItemSubmenuNode(i);
            if (sub != null)
            {
                Collect(sub, path + " > " + text.TrimEnd('.', '…'), depth + 1);
                continue;
            }

            if (menu.IsItemDisabled(i))
            {
                continue;
            }

            int id = menu.GetItemId(i);
            string title = text.TrimEnd('.', '…');
            string shortcut = menu.GetItemShortcut(i)?.GetAsText() ?? "";
            string full = path + " > " + title;
            var entry = new SearchEntry
            {
                Kind = "Menu",
                Title = title,
                Hint = shortcut.Length > 0 ? $"{path}   ({shortcut})" : path,
                Tags = "godot editor menu",
                Key = "Menu:" + full,
                Bonus = 20,
                Run = () => GodotUi.Press(menu, id),
            };
            _entries.Add(entry.Prepare());
            _byTitle[title] = (menu, id);
        }
    }

    /// <summary>Runs a menu item by its text, for the settings dialogs. False if there is none.</summary>
    public bool Press(string title)
    {
        Refresh();
        if (!_byTitle.TryGetValue(title, out var item))
        {
            return false;
        }

        GodotUi.Press(item.Menu, item.Id);
        return true;
    }
}

/// <summary>The main screens: 2D, 3D, Script, Game, AssetLib and the UO World tab.</summary>
public sealed class GodotScreenProvider : SearchProvider
{
    private static readonly string[] Known = { "2D", "3D", "Script", "Game", "AssetLib", GuoEditorPlugin.WorldTabName };
    private readonly List<SearchEntry> _entries = new();

    public override string Name => "screens";

    public override IEnumerable<SearchEntry> Entries => _entries;

    public override IEnumerable<SearchEntry> Suggested => _entries.Take(6);

    public override void Refresh()
    {
        _entries.Clear();
        var names = new List<string>();
        foreach (HBoxContainer box in GodotUi.All<HBoxContainer>(GodotUi.Base))
        {
            List<string> buttons = box.GetChildren().OfType<Button>()
                .Where(b => Known.Contains(b.Text)).Select(b => b.Text).ToList();
            if (buttons.Count >= 3)
            {
                names = buttons;
                break;
            }
        }

        foreach (string n in names.Count > 0 ? names : Known.ToList())
        {
            string name = n;
            _entries.Add(new SearchEntry
            {
                Kind = "Screen",
                Title = name,
                Hint = "main screen",
                Tags = "workspace tab editor main screen",
                Key = "Screen:" + name,
                Bonus = 30,
                Run = () => EditorInterface.Singleton.SetMainScreenEditor(name),
            }.Prepare());
        }
    }
}

/// <summary>
/// The panels along the bottom of the editor (Output, Debugger, Audio,
/// Animation, the shader editor, UO Shard...): the editor shows them as a
/// row of toggle buttons, and F3 presses the one asked for.
/// </summary>
public sealed class GodotPanelProvider : SearchProvider
{
    private readonly List<SearchEntry> _entries = new();

    public override string Name => "panels";

    public override IEnumerable<SearchEntry> Entries => _entries;

    public override void Refresh()
    {
        _entries.Clear();
        Node panel = GodotUi.Walk(GodotUi.Base).FirstOrDefault(n => n.GetClass() == "EditorBottomPanel");
        if (panel == null)
        {
            return;
        }

        foreach (HBoxContainer box in GodotUi.All<HBoxContainer>(panel))
        {
            List<Button> buttons = box.GetChildren().OfType<Button>().Where(b => b.ToggleMode && b.Text.Length > 0).ToList();
            if (buttons.Count < 2)
            {
                continue;
            }

            foreach (Button b in buttons)
            {
                Button button = b;
                string text = b.Text.Trim();
                _entries.Add(new SearchEntry
                {
                    Kind = "Panel",
                    Title = text,
                    Hint = "bottom panel",
                    Tags = "godot editor bottom panel dock",
                    Key = "Panel:" + text,
                    Bonus = 15,
                    Run = () =>
                    {
                        if (GodotObject.IsInstanceValid(button))
                        {
                            button.ButtonPressed = true;
                            button.EmitSignal(BaseButton.SignalName.Pressed);
                        }
                    },
                }.Prepare());
            }

            return;
        }
    }
}

/// <summary>
/// Editor Settings and Project Settings: every property of each, found by
/// name. Enter opens the settings dialog filtered to it; if the dialog
/// cannot be found it selects the property in the Inspector.
/// </summary>
public sealed class GodotSettingsProvider : SearchProvider
{
    private readonly GodotMenuProvider _menus;
    private readonly List<SearchEntry> _entries = new();
    private bool _built;

    public GodotSettingsProvider(GodotMenuProvider menus)
    {
        _menus = menus;
    }

    public override string Name => "settings";

    public override bool Done => _built;

    public override IEnumerable<SearchEntry> Entries => _entries;

    public override void Step(double ms)
    {
        _built = true;
        EditorSettings editor = EditorInterface.Singleton.GetEditorSettings();
        foreach (var prop in editor.GetPropertyList())
        {
            Add(prop, project: false);
        }

        foreach (var prop in ((GodotObject)ProjectSettings.Singleton).GetPropertyList())
        {
            Add(prop, project: true);
        }
    }

    private void Add(Godot.Collections.Dictionary prop, bool project)
    {
        string name = prop["name"].AsString();
        var usage = (PropertyUsageFlags)prop["usage"].AsInt32();
        if (!name.Contains('/') || name.StartsWith('_') || (usage & PropertyUsageFlags.Editor) == 0)
        {
            return;
        }

        string[] parts = name.Split('/');
        string title = GodotUi.Pretty(parts[^1]);
        string where = string.Join(" > ", parts.Take(parts.Length - 1).Select(GodotUi.Pretty));
        string dialog = project ? "Project Settings" : "Editor Settings";
        _entries.Add(new SearchEntry
        {
            Kind = "Setting",
            Title = title,
            Hint = $"{dialog} > {where}",
            Tags = name.Replace('/', ' ').Replace('_', ' ') + (project ? " project" : " editor") + " " + name,
            Key = (project ? "ProjectSetting:" : "EditorSetting:") + name,
            Run = () => Open(project, name),
        }.Prepare());
    }

    private void Open(bool project, string name)
    {
        string menuText = project ? "Project Settings" : "Editor Settings";
        string filter = name.Split('/')[^1].Replace('_', ' ');
        if (_menus.Press(menuText))
        {
            // The dialog is built when the menu item runs; give it a frame to show.
            GodotUi.AfterFrames(3, () =>
            {
                Window dialog = GodotUi.Walk(GodotUi.Base.GetTree().Root)
                    .OfType<Window>()
                    .FirstOrDefault(w => w.Visible && (w.GetClass() == (project ? "ProjectSettingsEditor" : "EditorSettingsDialog")));
                LineEdit search = dialog == null ? null : GodotUi.All<LineEdit>(dialog).FirstOrDefault();
                if (search != null)
                {
                    search.Text = filter;
                    search.EmitSignal(LineEdit.SignalName.TextChanged, filter);
                }
                else if (!project)
                {
                    EditorInterface.Singleton.InspectObject(EditorInterface.Singleton.GetEditorSettings(), name);
                }
            });
            return;
        }

        if (!project)
        {
            EditorInterface.Singleton.InspectObject(EditorInterface.Singleton.GetEditorSettings(), name);
        }
        else
        {
            SearchContext.Toast($"Project Settings could not be opened for {name}");
        }
    }
}
#endif
