#if TOOLS
namespace GUO.Editor;

using System;
using Godot;

/// <summary>
/// One tab of the UO Assets dock (docs/editor_plan.md §4.6). A panel reads
/// the install through <see cref="EditorData"/> once it has loaded, and
/// raises <see cref="Inspect"/> when the user picks something.
/// </summary>
[Tool]
public abstract partial class AssetPanel : VBoxContainer
{
    protected EditorData Data { get; private set; }

    /// <summary>Raised with what to show in the UO Inspector.</summary>
    public event Action<Inspection> Inspect;

    /// <summary>What tools/editor_smoke searches for on this panel.</summary>
    public abstract string SmokeQuery { get; }

    /// <summary>
    /// What a smoke inspection must carry: an image, or text only (cliloc,
    /// sounds).
    /// </summary>
    public virtual bool SmokeNeedsImage => true;

    public void Attach(EditorData data)
    {
        Data = data;
    }

    /// <summary>Called once, on the main thread, when the install has loaded.</summary>
    public abstract void OnDataLoaded();

    /// <summary>
    /// Runs a search as if typed, selects the first match and inspects it.
    /// Returns the id selected, or null.
    /// </summary>
    public abstract int? Search(string text);

    protected void Raise(Inspection inspection)
    {
        if (inspection != null)
        {
            Inspect?.Invoke(inspection);
        }
    }

    /// <summary>Stops anything the panel started (sounds). Called on teardown.</summary>
    public virtual void Shutdown()
    {
    }

    protected static bool TryParseId(string text, out int id)
    {
        id = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(
                text.AsSpan(2),
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out id
            );
        }

        return int.TryParse(
            text,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out id
        );
    }
}
#endif
