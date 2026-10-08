#if TOOLS
namespace GUO.Editor;

using System;
using Godot;

/// <summary>
/// The browse window behind an <see cref="AssetField"/>'s search button: the
/// UO Assets grid for the field's kind, opened on the current value. A
/// double-click or "Use selected" picks; Escape leaves the field as it was.
/// </summary>
public static class AssetPicker
{
    public static AcceptDialog Open(Node host, AssetPickKind kind, EditorData data, int current, Action<int> picked)
    {
        GridPanel panel = kind switch
        {
            AssetPickKind.Gump => new GumpPanel(),
            AssetPickKind.Static or AssetPickKind.Land => new ArtPanel(),
            AssetPickKind.Hue => new HuePanel(),
            AssetPickKind.Sound or AssetPickKind.Music => new SoundPanel(),
            AssetPickKind.Cliloc => new ClilocPanel(),
            _ => new MultiPanel(),
        };
        panel.MinimumGridHeight = 320;

        string noun = AssetCatalog.Noun(kind);
        var dialog = new AcceptDialog
        {
            Title = kind == AssetPickKind.Gump ? "Choose gump art" : $"Choose a {noun}",
            Size = (Vector2I)(new Vector2(860, 600) * EditorInterface.Singleton.GetEditorScale()),
            OkButtonText = "Use selected",
            DialogHideOnOk = false,
        };
        host.AddChild(dialog);
        panel.Attach(data);
        dialog.AddChild(panel);

        bool done = false;
        void Close()
        {
            if (done)
            {
                return;
            }

            done = true;
            dialog.Hide();
            panel.Shutdown();
            dialog.QueueFree();
        }

        void Choose(int id)
        {
            Close();
            AssetCatalog.Remember(kind, id);
            picked(id);
        }

        panel.Activated += Choose;
        dialog.Confirmed += () =>
        {
            if (panel.Selected is int id)
            {
                Choose(id);
            }
        };
        dialog.Canceled += Close;
        dialog.PopupCentered();

        void Fill()
        {
            if (panel is ArtPanel art)
            {
                art.SelectKind(kind == AssetPickKind.Land);
            }
            else if (panel is SoundPanel sound)
            {
                sound.SelectKind(kind == AssetPickKind.Music);
            }

            panel.OnDataLoaded();
            panel.Search(current > 0 || kind == AssetPickKind.Gump ? AssetCatalog.Format(kind, current) : "");
        }

        if (data?.IsLoaded == true)
        {
            Fill();
        }
        else if (data != null)
        {
            data.Loaded += Fill;
            dialog.TreeExiting += () => data.Loaded -= Fill;
        }

        return dialog;
    }
}
#endif
