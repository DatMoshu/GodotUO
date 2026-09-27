#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using GUO.Assets;

/// <summary>
/// Cliloc &amp; speech (plan §4.6 panel 6): search the localized strings in
/// any language the install has, and see which speech keywords the client
/// would detect in a phrase (<see cref="SpeechesLoader.GetKeywords"/>, the
/// call the client makes before it sends speech).
/// </summary>
[Tool]
public partial class ClilocPanel : GridPanel
{
    // Cliloc numbers in current installs stay below this.
    private const int MaxCliloc = 3_200_000;

    private OptionButton _kind, _lang;
    private List<int> _clilocs;
    private readonly Dictionary<int, SpeechEntry> _speech = new();

    public override string SmokeQuery => "backpack";

    public override bool SmokeNeedsImage => false;

    protected override int IconSize => 0;

    private bool Speech => _kind != null && _kind.Selected == 1;

    protected override string Placeholder => "cliloc number or text; in Speech, a phrase";

    protected override void BuildToolbar(HBoxContainer bar)
    {
        _kind = new OptionButton();
        _kind.AddItem("Cliloc");
        _kind.AddItem("Speech");
        _kind.ItemSelected += _ => Refresh();
        bar.AddChild(_kind);

        _lang = new OptionButton();
        _lang.ItemSelected += _ => SwitchLanguage();
        bar.AddChild(_lang);
    }

    public override void OnDataLoaded()
    {
        EnsureUi();
        _lang.Clear();
        string current = EditorData.Setting("UO_LANGUAGE", "enu").ToLowerInvariant();
        foreach (string file in Directory.GetFiles(Data.ClientData, "Cliloc.*").OrderBy(f => f))
        {
            string lang = Path.GetExtension(file).TrimStart('.').ToLowerInvariant();
            _lang.AddItem(lang);
            if (lang == current)
            {
                _lang.Selected = _lang.ItemCount - 1;
            }
        }

        base.OnDataLoaded();
    }

    private void SwitchLanguage()
    {
        string lang = _lang.GetItemText(_lang.Selected);
        Data.Files.Clilocs.Load(lang);
        _clilocs = null;
        Refresh();
    }

    protected override IEnumerable<int> Ids()
    {
        if (Speech)
        {
            _speech.Clear();
            string phrase = SearchBox.Text.Trim();
            if (phrase.Length > 0)
            {
                foreach (SpeechEntry e in Data.Files.Speeches.GetKeywords(phrase))
                {
                    _speech[e.KeywordID] = e;
                }
            }

            return _speech.Keys.OrderBy(k => k);
        }

        if (_clilocs == null)
        {
            _clilocs = new List<int>();
            ClilocLoader cl = Data.Files.Clilocs;
            for (int n = 0; n < MaxCliloc; n++)
            {
                if (cl.GetString(n) != null)
                {
                    _clilocs.Add(n);
                }
            }
        }

        return _clilocs;
    }

    private string Text(int id) =>
        Speech
            ? string.Join(" | ", _speech.TryGetValue(id, out SpeechEntry e) ? e.Keywords : Array.Empty<string>())
            : Data.Files.Clilocs.GetString(id) ?? "";

    protected override string Caption(int id)
    {
        string t = Text(id).Replace('\n', ' ');
        return $"{id}  {(t.Length > 90 ? t[..90] + "..." : t)}";
    }

    protected override bool Matches(int id, string query) =>
        Speech || Text(id).Contains(query, StringComparison.OrdinalIgnoreCase);

    protected override Inspection Describe(int id)
    {
        string lang = _lang.ItemCount > 0 ? _lang.GetItemText(_lang.Selected) : "";
        string text = Speech
            ? $"[b]Speech keyword 0x{id:X4}[/b] ({id})\nmatches: {Text(id)}\nphrase: \"{SearchBox.Text.Trim()}\"\n"
            : $"[b]Cliloc {id}[/b] (0x{id:X}) [{lang}]\n{EscapeBbcode(Text(id))}\n";

        return Inspection.Still("Cliloc", id.ToString(), null, text);
    }

    private static string EscapeBbcode(string s) => s.Replace("[", "[lb]");
}
#endif
