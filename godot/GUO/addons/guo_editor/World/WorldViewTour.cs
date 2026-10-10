#if TOOLS
namespace GUO.Editor;

using System.Collections.Generic;
using Godot;

/// <summary>
/// Hooks for the editor tour (ED3): the World tab's own controls and its real input handler, so the tour
/// drives the same buttons, spin boxes and mouse events a person does. Nothing here changes behaviour.
/// </summary>
public partial class WorldView
{
    /// <summary>The handler the map viewport's GuiInput runs: brush strokes, clicks, wheel, keys, Alt and Shift variants.</summary>
    internal void TourInput(InputEvent e) => OnInput(e);

    /// <summary>The rail button of a tool.</summary>
    internal Button ToolButton(WorldTool tool) => _toolButtons.TryGetValue(tool, out Button b) ? b : null;

    /// <summary>A brush spin box by its label ("Size (tiles)", "Density (%)", "Spacing (tiles)", "Strength", "Seed", "Maximum slope", "Z / ground offset", "Visible Z min", "Visible Z max").</summary>
    internal SpinBox TourNumber(string label) => _numbers.TryGetValue(label, out SpinBox s) ? s : null;

    /// <summary>A workspace check box by its label ("Square footprint", "Fixed placement plane", "Ghost roofs", "Lock terrain", "Keep existing statics", "Avoid water").</summary>
    internal CheckBox TourCheck(string label) => _checks.TryGetValue(label, out CheckBox c) ? c : null;

    internal Control TourArtBox => _brush;
    internal Control TourHueBox => _hue;
    internal Control TourVariantRows => _variantRows;
    internal Control TourRail => _toolButtons.TryGetValue(WorldTool.Select, out Button b) ? b.GetParent<Control>() : null;
    internal TabContainer TourDetailTabs => _detailTabs;
    internal LineEdit TourLibrarySearch => FindLineEdit(_brushArt);
    internal LineEdit TourWeights => _weights;
    internal LineEdit TourAllowed => _allowed;
    internal LineEdit TourEdges => _edges;
    internal LineEdit TourPresetName => _presetName;
    internal ItemList TourPresets => _presets;
    internal ItemList TourStack => _stack;
    internal OptionButton TourOperation => _operation;
    internal OptionButton TourTarget => _targetPick;
    internal Control TourLibrary => _library;
    internal Control TourSettings => _settings;
    internal Control TourLeft => _leftWorkspace;
    internal Control TourQuickFavorites => _quickFavorites;
    internal Control TourStage => _stage;
    internal Label TourPreviewLabel => _previewLabel;
    internal Label TourStatus => _status;
    internal CheckBox TourPinNearby => _pinNearby;
    internal Button[] TourNearbyCells => _nearbyCells;
    internal int TourStackIndex => _stackIndex;
    internal int TourFavoriteCount => _favorites.Count;
    internal string TourVariants => _recipe.Variants;
    internal (int Size, int Density, int Spacing, int Strength, int Seed, bool Square) TourRecipe =>
        (_recipe.Size, _recipe.Density, _recipe.Spacing, _recipe.Strength, _recipe.Seed, _recipe.Square);

    /// <summary>Shows the Tools tab (0) or the Advanced tab (1) of the settings panel.</summary>
    internal void TourSettingsTab(int tab)
    {
        if (_settings is TabContainer t) t.CurrentTab = tab;
    }

    /// <summary>The first visible button anywhere in the World tab whose text is exactly <paramref name="text"/>.</summary>
    internal Button TourButton(string text) => FindButton(this, text);

    private static Button FindButton(Node root, string text)
    {
        foreach (Node c in root.GetChildren())
        {
            if (c is Button b and not OptionButton and not MenuButton && b.Text == text && b.IsVisibleInTree()) return b;
            Button inner = FindButton(c, text);
            if (inner != null) return inner;
        }

        return null;
    }

    private static LineEdit FindLineEdit(Node root)
    {
        foreach (Node c in root.GetChildren())
        {
            if (c is LineEdit e) return e;
            LineEdit inner = FindLineEdit(c);
            if (inner != null) return inner;
        }

        return null;
    }

    /// <summary>The toolbar text buttons in the command row and the library, for naming a missing control in a failed check.</summary>
    internal List<string> TourButtonNames()
    {
        var names = new List<string>();
        void Walk(Node n)
        {
            foreach (Node c in n.GetChildren())
            {
                if (c is Button b && b.Text.Length > 0 && b.IsVisibleInTree()) names.Add(b.Text);
                Walk(c);
            }
        }

        Walk(this);
        return names;
    }
}
#endif
