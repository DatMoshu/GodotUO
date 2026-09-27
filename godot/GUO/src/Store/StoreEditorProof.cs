// SPDX-License-Identifier: BSD-2-Clause
#if TOOLS
using Godot;

namespace GUO.Store;

/// <summary>Opt-in editor evidence board; never enabled by the normal client.</summary>
[Tool]
public sealed partial class StoreEditorProof : Node
{
    public override async void _Ready()
    {
        string directory = System.Environment.GetEnvironmentVariable("GUO_STORE_EDITOR_PROOF");
        if (!Engine.IsEditorHint() || string.IsNullOrEmpty(directory)) return;
        DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.NoFocus, true);
        DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.AlwaysOnTop, false);
        for (int i = 0; i < 120; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color("141917"), ContentMarginTop = 24, ContentMarginBottom = 24, ContentMarginLeft = 24, ContentMarginRight = 24 });
        EditorInterface.Singleton.GetBaseControl().AddChild(panel);
        panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        panel.OffsetLeft = 320; panel.OffsetRight = -320;
        panel.OffsetTop = 130; panel.OffsetBottom = -240;
        var column = new VBoxContainer(); panel.AddChild(column);
        column.AddChild(new Label { Text = "GodotUO Asset Store — captured runtime evidence", HorizontalAlignment = HorizontalAlignment.Center });
        column.AddChild(new Label { Text = "Live Store client and installed background. These are captures, not an editor Store dock.", HorizontalAlignment = HorizontalAlignment.Center });
        var row = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill }; column.AddChild(row);
        foreach (string name in new[] { "store-client.png", "store-installed-background.png" })
        {
            var preview = new TextureRect {
                Texture = ImageTexture.CreateFromImage(Image.LoadFromFile(System.IO.Path.Combine(directory, name))),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill
            };
            row.AddChild(preview);
        }
        for (int i = 0; i < 30; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        Error result = EditorInterface.Singleton.GetBaseControl().GetViewport().GetTexture().GetImage()
            .SavePng(System.IO.Path.Combine(directory, "store-godot-editor.png"));
        GD.Print("[store editor proof] " + result);
        panel.QueueFree();
        GetTree().Quit(result == Error.Ok ? 0 : 1);
    }
}
#endif
