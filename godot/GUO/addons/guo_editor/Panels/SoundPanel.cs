#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;

/// <summary>
/// Sounds &amp; music (plan §4.6 panel 7). Effects and music play through the
/// game's own audio: <c>Renderer.Sounds.Sound</c> hands out the same
/// <c>UOSound</c> / <c>UOMusic</c> objects the client plays (ADR-0005).
/// </summary>
[Tool]
public partial class SoundPanel : GridPanel
{
    private const int MaxSound = 0x1000;
    private const int MaxMusic = 0x100;

    private OptionButton _kind;
    private readonly List<int>[] _ids = new List<int>[2];
    private readonly Dictionary<int, (string Name, int Bytes)> _effects = new();
    private readonly Dictionary<int, (string Name, bool Loop)> _music = new();
    private IO.Audio.Sound _playing;
    private readonly HashSet<IO.Audio.Sound> _played = new();

    public override string SmokeQuery => Data?.IsLoaded == true && Ids().Any() ? $"0x{Ids().First():X4}" : "0x0000";

    public override bool SmokeNeedsImage => false;

    protected override int IconSize => 0;

    /// <summary>Switches between Effects and Music (F3 opens an id in the right one).</summary>
    public void SelectKind(bool music)
    {
        EnsureUi();
        _kind.Selected = music ? 1 : 0;
    }

    private bool Music => _kind != null && _kind.Selected == 1;

    protected override string Placeholder => "id (0x0055) or name";

    protected override void BuildToolbar(HBoxContainer bar)
    {
        _kind = new OptionButton();
        _kind.AddItem("Effects");
        _kind.AddItem("Music");
        _kind.ItemSelected += _ => Refresh();
        bar.AddChild(_kind);

        var stop = new Button { Text = "Stop" };
        stop.Pressed += Stop;
        bar.AddChild(stop);
    }

    protected override IEnumerable<int> Ids()
    {
        int k = Music ? 1 : 0;
        if (_ids[k] != null)
        {
            return _ids[k];
        }

        var list = new List<int>();
        if (Music)
        {
            for (int i = 0; i < MaxMusic; i++)
            {
                if (Data.Files.Sounds.TryGetMusicData(i, out string name, out bool loop))
                {
                    _music[i] = (name, loop);
                    list.Add(i);
                }
            }
        }
        else
        {
            for (int i = 0; i < MaxSound; i++)
            {
                if (Data.Files.Sounds.TryGetSound(i, out byte[] data, out string name) && data?.Length > 0)
                {
                    _effects[i] = (name.TrimEnd('\0', ' '), data.Length);
                    list.Add(i);
                }
            }
        }

        return _ids[k] = list;
    }

    private string NameOf(int id) =>
        Music
            ? _music.TryGetValue(id, out var m) ? m.Name : ""
            : _effects.TryGetValue(id, out var e) ? e.Name : "";

    protected override string Caption(int id) => $"0x{id:X4}  {NameOf(id)}";

    protected override bool Matches(int id, string query) =>
        NameOf(id).Contains(query, StringComparison.OrdinalIgnoreCase);

    protected override Inspection Describe(int id)
    {
        var sb = new StringBuilder();
        if (Music)
        {
            _music.TryGetValue(id, out var m);
            sb.Append($"[b]Music 0x{id:X4}[/b] ({id})\nname {m.Name}   loop {m.Loop}\n");
        }
        else
        {
            _effects.TryGetValue(id, out var e);
            // 16-bit mono at 22050 Hz, what UOSound declares.
            double seconds = e.Bytes / 2.0 / 22050.0;
            sb.Append($"[b]Sound 0x{id:X4}[/b] ({id})\nname {e.Name}\n{e.Bytes} bytes, {seconds:0.00} s at 22050 Hz mono\n");
        }

        bool music = Music;
        var inspection = Inspection.Still("Sounds", $"0x{id:X4}", null, sb.ToString());
        inspection.Actions.Add(("Play", () => Play(id, music)));
        inspection.Actions.Add(("Stop", Stop));
        return inspection;
    }

    /// <summary>Plays a sound or a piece of music. Returns false if the game's audio would not.</summary>
    public bool Play(int id, bool music)
    {
        Stop();
        _playing = music ? Data.Sounds.GetMusic(id) : Data.Sounds.GetSound(id);
        if (_playing != null)
        {
            _played.Add(_playing);
        }

        return _playing != null && _playing.Play((uint)Time.GetTicksMsec());
    }

    /// <summary>For the smoke check: plays the selection and reports what happened.</summary>
    public string SmokePlay()
    {
        if (!Selected.HasValue)
        {
            return "nothing selected to play";
        }

        return Play(Selected.Value, Music) ? null : $"sound 0x{Selected.Value:X4} did not start";
    }

    private void Stop()
    {
        _playing?.Stop();
    }

    public override void Shutdown()
    {
        // Frees the AudioStreamPlayer the game's audio parented under the
        // editor's root.
        foreach (IO.Audio.Sound s in _played)
        {
            s.Stop();
            s.Dispose();
        }

        _played.Clear();
        _playing = null;
    }
}
#endif
