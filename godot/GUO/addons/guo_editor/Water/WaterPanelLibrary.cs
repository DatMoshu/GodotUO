// SPDX-License-Identifier: BSD-2-Clause
#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using GUO.Store;

internal partial class WaterPanel
{
    private sealed record LibraryEntry(StoreWater Style, string Path, string Key);
    private readonly List<LibraryEntry> _libraryEntries = new();
    private readonly List<LibraryEntry> _libraryRows = new();
    private VBoxContainer _library;
    private Tree _libraryTree;
    private LineEdit _librarySearch;
    private OptionButton _libraryModel, _librarySource;
    private Label _libraryCount, _libraryInfo;
    private Button _libraryLoad, _libraryReference;
    private string _libraryKey;
    private int _libraryUnreadable;
    internal int LibraryCount => _libraryRows.Count;
    internal LineEdit LibrarySearch => _librarySearch;
    internal OptionButton LibraryModel => _libraryModel;
    internal OptionButton LibrarySource => _librarySource;
    internal Tree LibraryTree => _libraryTree;
    internal string LibraryInfo => _libraryInfo.Text;
    internal bool LibraryOpen => _library.Visible;

    private static string ModelName(string model) => model switch
    { "uo" => "UO realtime", "pixel" => "Pixel", "cel" => "Cel", "realistic" => "Realistic", _ => "UO+" };

    private void BuildLibrary(Node body)
    {
        AddButton(body, "Browse presets", ShowLibrary).TooltipText = "Search starter presets and saved drafts, or compare one without replacing your work.";
        _library = new VBoxContainer { Visible = false }; body.AddChild(_library);
        _librarySearch = new LineEdit { PlaceholderText = "Search preset names", ClearButtonEnabled = true };
        _library.AddChild(_librarySearch); _librarySearch.TextChanged += _ => FilterLibrary();
        var filters = new HBoxContainer(); _library.AddChild(filters);
        _libraryModel = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = "Filter by water model" };
        _libraryModel.AddItem("All models");
        foreach (string model in StoreWater.Models) _libraryModel.AddItem(ModelName(model));
        filters.AddChild(_libraryModel); _libraryModel.ItemSelected += _ => FilterLibrary();
        _librarySource = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = "Filter by preset source" };
        foreach (string source in new[] { "All sources", "Starter presets", "Saved drafts" }) _librarySource.AddItem(source);
        filters.AddChild(_librarySource); _librarySource.ItemSelected += _ => FilterLibrary();
        var actions = new HFlowContainer(); _library.AddChild(actions);
        _libraryLoad = AddButton(actions, "Load selected", () => LibraryAction(false));
        _libraryReference = AddButton(actions, "Use as reference", () => LibraryAction(true));
        _libraryReference.TooltipText = "Compare the selected preset on the left. Retains your current draft, save destination and undo history.";
        AddButton(actions, "Close browser", () => _library.Hide());
        _libraryCount = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart }; _library.AddChild(_libraryCount);
        _libraryTree = new Tree { Columns = 3, HideRoot = true, ColumnTitlesVisible = true,
            CustomMinimumSize = new Vector2(0, 160), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        foreach (var (column, title, minimum) in new[] { (0, "Preset", 120), (1, "Model", 65), (2, "Source", 65) })
        { _libraryTree.SetColumnTitle(column, title); _libraryTree.SetColumnCustomMinimumWidth(column, minimum); }
        _libraryTree.SelectMode = Tree.SelectModeEnum.Row;
        _library.AddChild(_libraryTree); _libraryTree.ItemSelected += LibrarySelected;
        _libraryTree.ItemActivated += () => LibraryAction(false);
        _libraryInfo = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart }; _library.AddChild(_libraryInfo);

    }

    internal void ShowLibrary()
    {
        RefreshPresets();
        _library.Show();
        // Start at the library itself, so a short World tool area shows results,
        // rather than spending its height on the authoring header above it.
        Callable.From(() => { if (_library.Visible) ScrollVertical = (int)_library.Position.Y; }).CallDeferred();
    }

    private void RefreshLibrary(int unreadable)
    {
        _libraryUnreadable = unreadable;
        _libraryEntries.Clear();
        for (int i = 0; i < WaterAuthoring.Presets.Length; i++)
            _libraryEntries.Add(new(WaterAuthoring.Presets[i], null, "preset:" + i));
        foreach (var draft in _drafts) _libraryEntries.Add(new(draft.Style, draft.Path, draft.Path));
        FilterLibrary();
    }

    private void FilterLibrary()
    {
        _libraryRows.Clear(); _libraryTree.Clear();
        var root = _libraryTree.CreateItem();
        string search = _librarySearch.Text.Trim();
        int selected = 0;
        foreach (var entry in _libraryEntries)
        {
            if (!entry.Style.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || _libraryModel.Selected > 0 && entry.Style.Model != StoreWater.Models[_libraryModel.Selected - 1]
                || _librarySource.Selected == 1 && entry.Path != null
                || _librarySource.Selected == 2 && entry.Path == null) continue;
            int index = _libraryRows.Count; _libraryRows.Add(entry);
            if (entry.Key == _libraryKey) selected = index;
            var row = _libraryTree.CreateItem(root); row.SetMetadata(0, index);
            row.SetText(0, entry.Style.Name); row.SetText(1, ModelName(entry.Style.Model));
            row.SetText(2, entry.Path == null ? "Starter" : "Draft");
            row.SetTooltipText(0, entry.Path == null ? "Included starter preset" : entry.Path);
        }
        _libraryCount.Text = $"{_libraryRows.Count} of {_libraryEntries.Count} presets"
            + (_libraryUnreadable > 0 ? $". Skipped {_libraryUnreadable} unreadable draft(s)." : "");
        if (_libraryRows.Count > 0) root.GetChild(selected).Select(0);
        LibrarySelected();
    }

    private LibraryEntry SelectedLibraryEntry()
    {
        var row = _libraryTree.GetSelected();
        return row == null ? null : _libraryRows[(int)row.GetMetadata(0)];
    }

    private void LibrarySelected()
    {
        var entry = SelectedLibraryEntry();
        _libraryLoad.Disabled = _libraryReference.Disabled = entry == null;
        if (entry == null)
        { _libraryInfo.Text = "No matching presets. Clear the search or choose All models and All sources."; return; }
        _libraryKey = entry.Key;
        var s = entry.Style;
        _libraryInfo.Text = $"{s.Name} — {ModelName(s.Model)}\nWave {s.Wave:0.##} · speed {s.Speed:0.##} · foam {s.Foam:0.##}\nRipple {s.RippleStrength:0.##} · retention {s.RippleDamping:0.###}\n"
            + (entry.Path == null ? "Starter preset. Load to edit a copy." : "Saved draft. Load to edit its working file; Duplicate saves a separate copy.");
    }

    internal void LibraryAction(bool reference)
    {
        var entry = SelectedLibraryEntry();
        if (entry == null) return;
        try
        {
            // Re-read selected files: a stale browser row must not hide a changed or missing draft.
            StoreWater style = entry.Path == null ? entry.Style : WaterAuthoring.Read(entry.Path);
            if (reference)
            {
                SetReference(style); _compare.ButtonPressed = true; UpdateReference();
                _status.Text = "Reference set to " + style.Name + ". Your working draft is unchanged.";
            }
            else
            {
                LoadStyle(style, entry.Path);
                _status.Text = "Preset loaded. Undo restores the previous draft.";
            }
            _library.Hide(); ScrollVertical = 0;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        { _libraryInfo.Text = "Could not open this preset. Close and reopen the browser to refresh, or select another preset. " + ex.Message; }
    }
}
#endif
