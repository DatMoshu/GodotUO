#if TOOLS
namespace GUO.Editor;

using System;
using Godot;

/// <summary>
/// A text box for a UO id, like Unity's object field. A search button sits
/// inside the box on the left and opens a browse window filtered to the
/// field's kind. Typing suggests matches as F3 does: by hex ("0x0E75", "e75"),
/// decimal ("3701") or name ("torch"), with a picture of each. The art or
/// hue swatch sits beside the value; clicking it shows the asset in UO Assets.
/// A matching asset dragged from UO Assets drops on the field.
/// </summary>
/// <remarks>
/// <see cref="Committed"/> fires once per choice: Enter, a click on a
/// suggestion, a pick in the browse window, a drop, or leaving the box with a
/// changed value. A value the install does not have is refused, and the box
/// says so. Down or Ctrl+Space shows the recent picks before anything is
/// typed; Ctrl+Enter opens the browse window.
/// </remarks>
[Tool]
public partial class AssetField : PanelContainer
{
    /// <summary>Shows an id in the UO Assets dock. The plugin sets it; a click on the picture calls it.</summary>
    public static Action<AssetPickKind, int> Reveal { get; set; }

    private readonly EditorData _data;
    private AssetPickKind _kind;
    private int _value;
    private Button _browse;
    private TextureRect _thumb;
    private LineEdit _edit;
    private Label _name;
    private AssetSuggest _suggest;
    private bool _ready;
    private bool _theming;

    public AssetField() : this(null, AssetPickKind.Static)
    {
    }

    public AssetField(EditorData data, AssetPickKind kind, int value = 0)
    {
        _data = data;
        _kind = kind;
        _value = value;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        MouseFilter = MouseFilterEnum.Stop;
    }

    /// <summary>A choice was made: the new id.</summary>
    public event Action<int> Committed;

    /// <summary>0 is a value (a hue field's "no hue"), not an empty field.</summary>
    public bool AllowZero { get; set; }

    /// <summary>
    /// The box empties after each choice and keeps the focus, for typing one
    /// id after another (Gump Studio's "Add gump art" box). The value is not kept.
    /// </summary>
    public bool QuickAdd { get; set; }

    /// <summary>Enter raises <see cref="Committed"/> even when the value did not change (an "open this id" box).</summary>
    public bool AlwaysCommit { get; set; }

    /// <summary>Shown when the box is empty.</summary>
    public string Placeholder { get; set; } = "";

    public AssetPickKind Kind
    {
        get => _kind;
        set
        {
            _kind = value;
            Display();
        }
    }

    public int Value
    {
        get => _value;
        set => SetValue(value, false);
    }

    internal LineEdit Edit => _edit;

    internal AssetSuggest Suggest => _suggest;

    internal EditorData DataSource => _data;

    private AssetCatalog Catalog => AssetCatalog.Of(_data);

    private static float UiScale => EditorInterface.Singleton.GetEditorScale();

    public override void _Ready()
    {
        if (_ready)
        {
            return;
        }

        _ready = true;
        TextureFilter = TextureFilterEnum.Nearest;
        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Pass };
        row.AddThemeConstantOverride("separation", (int)(4 * UiScale));
        AddChild(row);

        _browse = new Button
        {
            Flat = true,
            FocusMode = FocusModeEnum.None,
            TooltipText = $"Browse {AssetCatalog.Noun(_kind)}s (Ctrl+Enter)",
            MouseDefaultCursorShape = CursorShape.PointingHand,
        };
        _browse.Pressed += OpenBrowser;
        row.AddChild(_browse);

        _thumb = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            // Art is never filtered (AGENTS.md rule 7).
            TextureFilter = TextureFilterEnum.Nearest,
            MouseFilter = MouseFilterEnum.Stop,
            MouseDefaultCursorShape = CursorShape.PointingHand,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        _thumb.GuiInput += e =>
        {
            if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } && HasValue)
            {
                Reveal?.Invoke(_kind, _value);
            }
        };
        row.AddChild(_thumb);

        _edit = new LineEdit
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(64 * UiScale, 0),
            SelectAllOnFocus = true,
            ContextMenuEnabled = true,
        };
        foreach (string style in new[] { "normal", "focus", "read_only" })
        {
            _edit.AddThemeStyleboxOverride(style, new StyleBoxEmpty());
        }

        _edit.TextChanged += OnTyped;
        _edit.GuiInput += OnKey;
        _edit.FocusEntered += () =>
        {
            _name.Visible = false;
            QueueRedraw();
        };
        _edit.FocusExited += OnFocusLost;
        row.AddChild(_edit);

        _name = new Label
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 1.6f,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        row.AddChild(_name);

        // A drag over the text or the picture lands on the field, not on the LineEdit's own text drop.
        var canDrop = Callable.From<Vector2, Variant, bool>(_CanDropData);
        var drop = Callable.From<Vector2, Variant>(_DropData);
        _edit.SetDragForwarding(default, canDrop, drop);
        _thumb.SetDragForwarding(default, canDrop, drop);

        _suggest = new AssetSuggest(this);
        _suggest.Picked += id => Choose(id, true);

        if (_data != null)
        {
            _data.Loaded += OnDataLoaded;
        }

        ApplyTheme();
        Display();
    }

    public override void _ExitTree()
    {
        _suggest?.Close();
    }

    public override void _Notification(int what)
    {
        if (what == NotificationThemeChanged && _ready)
        {
            ApplyTheme();
        }
        else if (what == NotificationPredelete)
        {
            if (_data != null)
            {
                _data.Loaded -= OnDataLoaded;
            }

            _suggest?.Discard();
        }
    }

    private void OnDataLoaded() => Display();

    /// <summary>The box draws as a LineEdit: the editor's own field frame, and its focus ring while typing.</summary>
    private void ApplyTheme()
    {
        // An override on this control raises its own theme-changed notification.
        if (_theming)
        {
            return;
        }

        _theming = true;
        AddThemeStyleboxOverride("panel", GetThemeStylebox("normal", "LineEdit"));
        Theme editor = EditorInterface.Singleton.GetEditorTheme();
        _browse.Icon = editor.GetIcon("Search", "EditorIcons");
        _name.AddThemeColorOverride("font_color", GetThemeColor("font_placeholder_color", "LineEdit"));
        _edit.PlaceholderText = Placeholder;
        _theming = false;
    }

    public override void _Draw()
    {
        if (_edit != null && _edit.HasFocus())
        {
            DrawStyleBox(GetThemeStylebox("focus", "LineEdit"), new Rect2(Vector2.Zero, Size));
        }
    }

    private bool HasValue => _value > 0 || (AllowZero && _value == 0 && _kind == AssetPickKind.Hue);

    /// <summary>Sets the value without raising <see cref="Committed"/> (or with it, when <paramref name="raise"/>).</summary>
    public void SetValue(int id, bool raise)
    {
        _value = id;
        Display();
        if (raise)
        {
            Committed?.Invoke(id);
        }
    }

    /// <summary>Writes the value, its name and its picture into the box.</summary>
    private void Display(string problem = null)
    {
        if (!_ready)
        {
            return;
        }

        bool quiet = QuickAdd || (_value == 0 && !AllowZero);
        _edit.Text = quiet ? "" : AssetCatalog.Format(_kind, _value);
        _edit.PlaceholderText = Placeholder;
        _browse.TooltipText = $"Browse {AssetCatalog.Noun(_kind)}s (Ctrl+Enter)";

        AssetCatalog cat = Catalog;
        string name = problem ?? "";
        Texture2D thumb = null;
        if (problem == null && !quiet && cat != null && cat.Ready)
        {
            if (_kind == AssetPickKind.Hue && _value == 0)
            {
                name = "no hue";
            }
            else if (cat.Exists(_kind, _value))
            {
                name = cat.Name(_kind, _value);
                thumb = cat.Thumb(_kind, _value);
            }
            else
            {
                name = $"no {AssetCatalog.Noun(_kind)} {AssetCatalog.Format(_kind, _value)}";
            }
        }

        _name.Text = name;
        _name.TooltipText = name;
        _name.Visible = !_edit.HasFocus() && name.Length > 0;
        _name.RemoveThemeColorOverride("font_color");
        _name.AddThemeColorOverride("font_color", problem != null
            ? GetThemeColor("error_color", "Editor")
            : GetThemeColor("font_placeholder_color", "LineEdit"));

        _thumb.Texture = thumb;
        _thumb.Visible = thumb != null;
        float s = UiScale;
        _thumb.CustomMinimumSize = _kind == AssetPickKind.Hue ? new Vector2(32, 10) * s : new Vector2(20, 20) * s;
        _thumb.TooltipText = thumb != null ? $"{name}\nClick to show it in UO Assets" : "";
    }

    private void OnTyped(string text)
    {
        if (_edit.HasFocus())
        {
            _suggest.Open(text);
        }
    }

    private void OnKey(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true } k)
        {
            return;
        }

        bool handled = true;
        switch (k.Keycode)
        {
            case Key.Enter or Key.KpEnter when k.CtrlPressed:
                OpenBrowser();
                break;
            case Key.Enter or Key.KpEnter:
                CommitTyped();
                break;
            case Key.Space when k.CtrlPressed:
                _suggest.Open(_edit.Text, true);
                break;
            case Key.Down:
                if (_suggest.IsOpen)
                {
                    _suggest.Move(1);
                }
                else
                {
                    _suggest.Open(_edit.Text, true);
                }

                break;
            case Key.Up when _suggest.IsOpen:
                _suggest.Move(-1);
                break;
            case Key.Pagedown when _suggest.IsOpen:
                _suggest.Move(8);
                break;
            case Key.Pageup when _suggest.IsOpen:
                _suggest.Move(-8);
                break;
            case Key.Escape:
                if (_suggest.IsOpen)
                {
                    _suggest.Close();
                }
                else
                {
                    Display();
                    _edit.ReleaseFocus();
                }

                break;
            default:
                handled = false;
                break;
        }

        if (handled)
        {
            _edit.AcceptEvent();
        }
    }

    /// <summary>Enter: the highlighted suggestion, else what was typed if it is an id the install has, else the best name match.</summary>
    public void CommitTyped()
    {
        if (_suggest.IsOpen && _suggest.Current is int highlighted)
        {
            Choose(highlighted, true);
            return;
        }

        string text = _edit.Text.Trim();
        if (text.Length == 0)
        {
            _suggest.Close();
            if (!QuickAdd && AllowZero)
            {
                Choose(0, true);
            }
            else
            {
                Display();
            }

            return;
        }

        AssetCatalog cat = Catalog;
        if (cat == null || !cat.Ready)
        {
            Display("the client data is still loading");
            return;
        }

        if (AssetCatalog.ParseNumber(text) is int id)
        {
            if (cat.Exists(_kind, id) || (AllowZero && id == 0))
            {
                Choose(id, true);
            }
            else
            {
                Refuse($"no {AssetCatalog.Noun(_kind)} {AssetCatalog.Format(_kind, id)} in this install");
            }

            return;
        }

        var best = cat.Query(_kind, text, 1);
        if (best.Count > 0)
        {
            Choose(best[0].Id, true);
        }
        else
        {
            Refuse($"no {AssetCatalog.Noun(_kind)} matches \"{text}\"");
        }
    }

    private void Refuse(string why)
    {
        _suggest.Close();
        _name.Text = why;
        _name.TooltipText = why;
        _name.Visible = true;
        _name.AddThemeColorOverride("font_color", GetThemeColor("error_color", "Editor"));
    }

    /// <summary>A choice from any route: remember it, show it, tell the owner.</summary>
    private void Choose(int id, bool keepFocus)
    {
        _suggest.Close();
        if (id > 0 || !AllowZero)
        {
            AssetCatalog.Remember(_kind, id);
        }

        bool changed = id != _value || QuickAdd || (AlwaysCommit && keepFocus);
        _value = id;
        Display();
        if (QuickAdd && keepFocus)
        {
            _edit.GrabFocus();
        }

        if (changed)
        {
            Committed?.Invoke(id);
        }
    }

    private void OnFocusLost()
    {
        QueueRedraw();

        // Tab (or a click away) takes the highlighted suggestion, as Unity's field does.
        int? highlighted = _suggest.IsOpen ? _suggest.Current : null;

        // Deferred: a click on a suggestion is handled first, whatever the focus did.
        Callable.From(() =>
        {
            if (!GodotObject.IsInstanceValid(this) || _edit.HasFocus())
            {
                return;
            }

            _suggest.Close();
            string text = _edit.Text.Trim();
            string shown = (QuickAdd || (_value == 0 && !AllowZero)) ? "" : AssetCatalog.Format(_kind, _value);
            if (QuickAdd || text.Length == 0 || text == shown || Catalog?.Ready != true)
            {
                Display();
                return;
            }

            if (AssetCatalog.ParseNumber(text) is int id && (Catalog.Exists(_kind, id) || (AllowZero && id == 0)))
            {
                Choose(id, false);
            }
            else if (highlighted is int pick)
            {
                Choose(pick, false);
            }
            else
            {
                Display();
            }
        }).CallDeferred();
    }

    /// <summary>Opens the browse window on the current value.</summary>
    public void OpenBrowser()
    {
        _suggest.Close();
        AssetPicker.Open(this, _kind, _data, _value, id => Choose(id, QuickAdd));
    }

    private string DropKey => _kind switch
    {
        AssetPickKind.Gump => "guo_gump",
        AssetPickKind.Static => "guo_static",
        AssetPickKind.Hue => "guo_hue",
        _ => null,
    };

    public override bool _CanDropData(Vector2 atPosition, Variant data) =>
        DropKey is string key && data.VariantType == Variant.Type.Dictionary && data.AsGodotDictionary().ContainsKey(key);

    public override void _DropData(Vector2 atPosition, Variant data)
    {
        if (_CanDropData(atPosition, data))
        {
            Choose(data.AsGodotDictionary()[DropKey].AsInt32(), false);
        }
    }
}
#endif
