#if TOOLS
namespace GUO.Editor;

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Godot;

/// <summary>
/// Parity (plan §5 phase 1): one asset drawn by the guoasset reference
/// renderer beside GUO's own decode, with a pixel diff. The reference is
/// UOWW's <c>uoasset</c> CLI, the program behind the guoasset MCP server
/// (tools/guoasset/README.md), so it reads the same install independently of
/// the port.
/// </summary>
/// <remarks>
/// <para>
/// Only <c>uoasset get-image</c> is ever run. The same CLI has verbs that
/// write into the client folder (<c>pack</c>, <c>import-multi</c>,
/// <c>add-asset-prod</c>, and <c>backup</c>, which defaults to a folder inside
/// it); GUO never writes to <c>UO_CLIENT_DATA</c>, so the verb is fixed here
/// and not built from input.
/// </para>
/// <para>
/// Multis compare against the Multis panel's composite, which is a preview
/// and not the world renderer; a difference there says the preview differs,
/// not the game. Phase 2's World tab is where a renderer comparison belongs.
/// </para>
/// </remarks>
[Tool]
public partial class ParityPanel : AssetPanel
{
    private static readonly string[] Kinds = { "static", "land", "gump", "multi" };

    private OptionButton _kind;
    private LineEdit _id;
    private Button _go;
    private Label _status;
    private bool _busy;

    public override string SmokeQuery => "0x0E75";

    /// <summary>Why the smoke check cannot run this panel, or null.</summary>
    public string Unavailable => File.Exists(Cli()) ? null : $"uoasset CLI not found at {Cli()} (UOWW_ROOT); parity is optional";

    public override void _Ready() => EnsureUi();

    private void EnsureUi()
    {
        if (_kind != null)
        {
            return;
        }

        var bar = new HBoxContainer();
        AddChild(bar);
        _kind = new OptionButton();
        foreach (string k in Kinds)
        {
            _kind.AddItem(k);
        }

        bar.AddChild(_kind);
        _id = new LineEdit { PlaceholderText = "id (0x0E75)", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _id.TextSubmitted += _ => CompareAsync();
        bar.AddChild(_id);
        _go = new Button { Text = "Compare" };
        _go.Pressed += CompareAsync;
        bar.AddChild(_go);

        _status = new Label
        {
            Text = "reference: guoasset (uoasset get-image). Reads the install; never writes it.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        AddChild(_status);
    }

    public override void OnDataLoaded()
    {
        EnsureUi();
        if (Unavailable != null)
        {
            _status.Text = Unavailable;
        }
    }

    private static string Cli()
    {
        string over = EditorData.Setting("UOASSET_CLI", "");
        if (over.Length > 0)
        {
            return over;
        }

        string root = EditorData.Setting("UOWW_ROOT", "");
        return Path.Combine(root, "tools", "UOAssetStudio", "src", "UOAssetStudio.Cli", "bin", "Debug", "net10.0", "uoasset.exe");
    }

    private static string OutDir() =>
        Path.GetFullPath(Path.Combine(ProjectSettings.GlobalizePath("res://"), "..", "..", "build", "editor_parity"));

    /// <summary>For the smoke check: compares synchronously. Returns a nonzero id on success.</summary>
    public override int? Search(string text)
    {
        EnsureUi();
        _kind.Selected = 0;
        _id.Text = text;
        if (!TryParseId(text, out int id))
        {
            return null;
        }

        (string refPath, string error) = Reference(Kinds[_kind.Selected], id);
        return Show(Kinds[_kind.Selected], id, refPath, error) ? id : null;
    }

    private void CompareAsync()
    {
        if (_busy || !TryParseId(_id.Text, out int id))
        {
            _status.Text = _busy ? "already comparing" : "type an id";
            return;
        }

        string kind = Kinds[_kind.Selected];
        _busy = true;
        _go.Disabled = true;
        _status.Text = $"rendering the reference for {kind} 0x{id:X4}...";
        Task.Run(() =>
        {
            (string refPath, string error) = Reference(kind, id);
            Callable.From(() =>
            {
                _busy = false;
                _go.Disabled = false;
                Show(kind, id, refPath, error);
            }).CallDeferred();
        });
    }

    /// <summary>Runs <c>uoasset get-image</c> and returns the PNG it wrote.</summary>
    private static (string Path, string Error) Reference(string kind, int id)
    {
        string cli = Cli();
        if (!File.Exists(cli))
        {
            return (null, $"uoasset CLI not found: {cli}");
        }

        Directory.CreateDirectory(OutDir());
        var psi = new ProcessStartInfo(cli)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // get-image only: see the class remarks.
        psi.ArgumentList.Add("get-image");
        psi.ArgumentList.Add("--client");
        psi.ArgumentList.Add(EditorData.Setting("UO_CLIENT_DATA", ""));
        if (kind == "multi")
        {
            psi.ArgumentList.Add("--multi");
            psi.ArgumentList.Add($"0x{id:X4}");
        }
        else
        {
            psi.ArgumentList.Add("--tile");
            psi.ArgumentList.Add($"0x{id:X4}");
            psi.ArgumentList.Add("--kind");
            psi.ArgumentList.Add(kind);
        }

        psi.ArgumentList.Add("--out");
        psi.ArgumentList.Add(OutDir());

        try
        {
            using Process p = Process.Start(psi);
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(60_000))
            {
                p.Kill();
                return (null, "uoasset timed out");
            }

            foreach (string line in stdout.Split('\n'))
            {
                string t = line.Trim();
                if (t.StartsWith("png:", StringComparison.Ordinal))
                {
                    return (t.Substring(4).Trim(), null);
                }
            }

            return (null, $"uoasset exit {p.ExitCode}: {(stderr + stdout).Trim()}");
        }
        catch (Exception ex)
        {
            return (null, $"uoasset failed: {ex.Message}");
        }
    }

    private Image Ours(string kind, int id) => kind switch
    {
        "static" => Data.ArtImage(EditorData.LandCount + (uint)id),
        "land" => Data.ArtImage((uint)id),
        "gump" => Data.GumpImage(id),
        "multi" => MultiPanel.CompositeOf(Data, id),
        _ => null,
    };

    private bool Show(string kind, int id, string refPath, string error)
    {
        string title = $"{kind} 0x{id:X4}";
        if (error != null)
        {
            _status.Text = error;
            Raise(Inspection.Still("Parity", title, null, $"[b]Parity {title}[/b]\n{error}\n"));
            return false;
        }

        var reference = new Image();
        if (reference.Load(refPath) != Error.Ok)
        {
            _status.Text = $"could not read {refPath}";
            return false;
        }

        reference.Convert(Image.Format.Rgba8);
        Image ours = Ours(kind, id);
        if (ours == null)
        {
            _status.Text = $"GUO has no image for {title}";
            Raise(Inspection.Still("Parity", title, reference, $"[b]Parity {title}[/b]\nreference only: GUO decodes nothing for this id\n"));
            return false;
        }

        (Image diff, int differing, int compared) = Diff(reference, ours);

        // reference | GUO | diff, side by side.
        int w = reference.GetWidth(), h = reference.GetHeight();
        int ow = ours.GetWidth(), oh = ours.GetHeight();
        int dw = diff.GetWidth(), dh = diff.GetHeight();
        Image sheet = Image.CreateEmpty(w + ow + dw + 16, Math.Max(h, Math.Max(oh, dh)), false, Image.Format.Rgba8);
        sheet.BlitRect(reference, new Rect2I(0, 0, w, h), Vector2I.Zero);
        sheet.BlitRect(ours, new Rect2I(0, 0, ow, oh), new Vector2I(w + 8, 0));
        sheet.BlitRect(diff, new Rect2I(0, 0, dw, dh), new Vector2I(w + ow + 16, 0));

        var sb = new StringBuilder();
        sb.Append($"[b]Parity {title}[/b]   reference | GUO | diff\n");
        sb.Append($"reference {w}x{h} (guoasset)   GUO {ow}x{oh}\n");
        sb.Append(w == ow && h == oh ? "sizes match\n" : "[color=orange]sizes differ; diffed over the overlap[/color]\n");
        sb.Append(differing == 0
            ? $"[color=green]identical: {compared} pixels[/color]\n"
            : $"[color=orange]{differing} of {compared} pixels differ[/color] (magenta in the diff)\n");
        if (kind == "multi")
        {
            sb.Append("GUO side is the Multis panel's preview composite, not the world renderer.\n");
        }

        sb.Append($"reference png: {refPath}\n");
        _status.Text = differing == 0 ? $"{title}: identical" : $"{title}: {differing} pixels differ";
        Raise(Inspection.Still("Parity", title, sheet, sb.ToString()));
        return true;
    }

    /// <summary>
    /// Pixel diff over the overlap: transparent in both, or same colour when
    /// both are opaque, counts as equal. Differences are magenta; equal
    /// pixels are the reference, dimmed.
    /// </summary>
    private static (Image, int, int) Diff(Image a, Image b)
    {
        int w = Math.Min(a.GetWidth(), b.GetWidth()), h = Math.Min(a.GetHeight(), b.GetHeight());
        Image d = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
        int differing = 0;
        var magenta = new Color(1, 0, 1);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                Color ca = a.GetPixel(x, y), cb = b.GetPixel(x, y);
                bool ta = ca.A8 == 0, tb = cb.A8 == 0;
                bool same = (ta && tb) || (!ta && !tb && ca.R8 == cb.R8 && ca.G8 == cb.G8 && ca.B8 == cb.B8);
                if (same)
                {
                    d.SetPixel(x, y, ta ? new Color(0, 0, 0, 0) : ca.Darkened(0.6f));
                }
                else
                {
                    differing++;
                    d.SetPixel(x, y, magenta);
                }
            }
        }

        return (d, differing, w * h);
    }
}
#endif
