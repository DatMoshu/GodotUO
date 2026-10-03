#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;

/// <summary>
/// The AI dock's Services tab (ADR-0028): every third-party service endpoint and key in one list,
/// the OpenAI-compatible endpoints, ComfyUI, Retro Diffusion and Ollama. Add, Edit, Remove, Test.
/// Keys go into the operating system's store through <see cref="ServiceBook"/>; nothing is written
/// into a project. Test asks a free, read-only endpoint (a model list, a status, a credit balance)
/// and never starts a paid generation.
/// </summary>
[Tool]
public partial class AiServicesTab : VBoxContainer
{
    private AiHub _hub;
    private ItemList _list;
    private Label _status;
    private ConfirmationDialog _dialog;
    private OptionButton _kind;
    private LineEdit _name, _url, _model, _key;
    private CheckBox _art;
    private Button _edit, _remove, _test;
    private bool _built;
    private IReadOnlyList<ServiceInfo> _shown = Array.Empty<ServiceInfo>();

    public AiServicesTab() : this(null)
    {
    }

    internal AiServicesTab(AiHub hub)
    {
        Name = "Services";
        _hub = hub;
        SizeFlagsVertical = SizeFlags.ExpandFill;
    }

    /// <summary>The last test result line, or the last error.</summary>
    public string StatusText => _status?.Text ?? "";

    /// <summary>The list as plain text, one row per line.</summary>
    public string ListText
    {
        get
        {
            var rows = new List<string>();
            for (int i = 0; _list != null && i < _list.ItemCount; i++)
            {
                rows.Add(_list.GetItemText(i));
            }

            return string.Join("\n", rows);
        }
    }

    public override void _Ready()
    {
        if (_built || _hub == null)
        {
            return;
        }

        _built = true;
        var top = new HBoxContainer();
        AddChild(top);
        var add = new Button { Text = "Add...", TooltipText = "Register an OpenAI-compatible endpoint, ComfyUI, Retro Diffusion or Ollama. Keys are kept in the operating system's store." };
        add.Pressed += () => OpenDialog(null);
        top.AddChild(add);
        _edit = new Button { Text = "Edit...", Disabled = true };
        _edit.Pressed += () => OpenDialog(Selected);
        top.AddChild(_edit);
        _remove = new Button { Text = "Remove", Disabled = true, TooltipText = "Forget the service and its key" };
        _remove.Pressed += () => RemoveSelected();
        top.AddChild(_remove);
        _test = new Button { Text = "Test", Disabled = true, TooltipText = "Ask a free read-only endpoint (a model list, a status, a credit balance). Never starts a generation." };
        _test.Pressed += () => _ = TestSelectedAsync();
        top.AddChild(_test);
        _status = new Label { Text = EndpointBook.CanKeepKeys ? "" : "this system keeps no keys; type them again each time", SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true };
        top.AddChild(_status);

        _list = new ItemList
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(300, 100),
            TextureFilter = TextureFilterEnum.Nearest,
        };
        _list.ItemSelected += _ => UpdateButtons();
        AddChild(_list);

        BuildDialog();
        _hub.ServicesChanged += Redraw;
        Redraw();
    }

    public override void _ExitTree()
    {
        if (_hub != null)
        {
            _hub.ServicesChanged -= Redraw;
        }
    }

    public ServiceInfo Selected
    {
        get
        {
            int[] sel = _list?.GetSelectedItems();
            return sel != null && sel.Length > 0 && sel[0] < _shown.Count ? _shown[sel[0]] : null;
        }
    }

    private void UpdateButtons()
    {
        bool has = Selected != null;
        _edit.Disabled = !has;
        _remove.Disabled = !has;
        _test.Disabled = !has;
    }

    private void Redraw()
    {
        if (_list == null)
        {
            return;
        }

        _shown = _hub.Services.List();
        _list.Clear();
        foreach (ServiceInfo s in _shown)
        {
            _list.AddItem($"{ServiceBook.KindLabel(s.Kind),-17} {s.Name}  |  {s.Url}  |  key {(s.HasKey ? "set" : "none")}{(s.AllowClientArt ? "  |  client art allowed" : "")}");
        }

        UpdateButtons();
    }

    /// <summary>Adds or replaces a service (the dialog's Save does this). False with the reason in the status line.</summary>
    public bool Add(ServiceKind kind, string name, string url, string model, string key, bool allowClientArt)
    {
        if (!_hub.Services.Put(kind, name, string.IsNullOrWhiteSpace(url) ? ServiceBook.DefaultUrl(kind) : url, model, key, allowClientArt, out string why))
        {
            SetStatus(why ?? "not saved", true);
            return false;
        }

        _hub.NotifyServices();
        SetStatus($"saved {name}");
        return true;
    }

    public bool Remove(ServiceKind kind, string name)
    {
        if (_hub.Services.Find(kind, name) == null)
        {
            SetStatus($"no service named {name}", true);
            return false;
        }

        _hub.Services.Remove(kind, name);
        _hub.NotifyServices();
        SetStatus($"removed {name}");
        return true;
    }

    private void RemoveSelected()
    {
        ServiceInfo s = Selected;
        if (s != null)
        {
            Remove(s.Kind, s.Name);
        }
    }

    /// <summary>Tests one service with its free read-only call. The answer is also shown in the status line.</summary>
    public async Task<(bool Ok, string Detail)> TestAsync(ServiceKind kind, string name)
    {
        ServiceInfo s = _hub.Services.Find(kind, name);
        if (s == null)
        {
            SetStatus($"no service named {name}", true);
            return (false, "no such service");
        }

        SetStatus($"testing {s.Name}...");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        (bool ok, string detail) = await Task.Run(() => _hub.Services.TestAsync(s, cts.Token));
        SetStatus($"{s.Name}: {(ok ? "ok, " : "failed, ")}{detail}", !ok);
        return (ok, detail);
    }

    private async Task TestSelectedAsync()
    {
        ServiceInfo s = Selected;
        if (s != null)
        {
            await TestAsync(s.Kind, s.Name);
        }
    }

    private void OpenDialog(ServiceInfo edit)
    {
        _kind.Disabled = edit != null;
        _name.Editable = edit == null;
        _kind.Select(edit != null ? (int)edit.Kind : 0);
        _name.Text = edit?.Name ?? "";
        _url.Text = edit?.Url ?? "";
        _model.Text = edit?.Model ?? "";
        _key.Text = "";
        _key.PlaceholderText = edit?.HasKey == true ? "(unchanged: kept in the operating system's store)"
            : EndpointBook.CanKeepKeys ? "kept in the operating system's store" : "this system keeps no keys";
        _art.ButtonPressed = edit?.AllowClientArt ?? false;
        _dialog.Title = edit == null ? "Add a service" : $"Edit {edit.Name}";
        OnKindChanged();
        if (edit != null)
        {
            _url.Text = edit.Url;
        }

        _dialog.PopupCentered(new Vector2I(520, 280));
    }

    private ServiceKind PickedKind => (ServiceKind)Math.Max(0, _kind.Selected);

    private void OnKindChanged()
    {
        ServiceKind k = PickedKind;
        _url.PlaceholderText = k == ServiceKind.OpenAi ? "http://127.0.0.1:1234/v1" : ServiceBook.DefaultUrl(k);
        _model.Editable = k == ServiceKind.OpenAi;
        _art.Visible = k is ServiceKind.OpenAi or ServiceKind.Ollama;
        if (_name.Text.Length == 0 && _name.Editable && k != ServiceKind.OpenAi)
        {
            _name.Text = ServiceBook.KindLabel(k);
        }
    }

    private void BuildDialog()
    {
        _dialog = new ConfirmationDialog { OkButtonText = "Save" };
        var grid = new GridContainer { Columns = 2 };
        _dialog.AddChild(grid);
        grid.AddChild(new Label { Text = "Kind" });
        _kind = new OptionButton();
        foreach (ServiceKind k in Enum.GetValues<ServiceKind>())
        {
            _kind.AddItem(ServiceBook.KindLabel(k));
        }

        _kind.ItemSelected += _ => OnKindChanged();
        grid.AddChild(_kind);
        grid.AddChild(new Label { Text = "Name" });
        _name = new LineEdit { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        grid.AddChild(_name);
        grid.AddChild(new Label { Text = "URL" });
        _url = new LineEdit { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        grid.AddChild(_url);
        grid.AddChild(new Label { Text = "Model" });
        _model = new LineEdit { PlaceholderText = "(optional, OpenAI-compatible only)" };
        grid.AddChild(_model);
        grid.AddChild(new Label { Text = "API key" });
        _key = new LineEdit { Secret = true };
        grid.AddChild(_key);
        grid.AddChild(new Label { Text = "" });
        _art = new CheckBox
        {
            Text = "Allow client art",
            TooltipText = "Let the Chat tab send asset pictures from your UO install to this server. Off unless it is yours or you trust it. A local Ollama needs no leave.",
        };
        grid.AddChild(_art);
        _dialog.Confirmed += () => Add(PickedKind, _name.Text, _url.Text, _model.Text.Trim(), _key.Text, _art.ButtonPressed && _art.Visible);
        AddChild(_dialog);
    }

    private void SetStatus(string text, bool error = false)
    {
        if (_status != null)
        {
            _status.Text = text;
            _status.AddThemeColorOverride("font_color", error ? new Color(1f, 0.6f, 0.3f) : new Color(0.7f, 0.7f, 0.7f));
        }
    }
}
#endif
