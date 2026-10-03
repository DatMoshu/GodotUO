// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.IO;
using Godot;
using GUO.Game;
using GUO.UI.Authoring;

namespace GUO.Input.Touch.Modern;

/// <summary>Hosts authored Godot UI on the same input/lifecycle path as other Modern gumps.</summary>
internal sealed partial class ModernAuthoredGump : ModernGump
{
    private readonly GumpDocument _document;
    private readonly Action<GumpReply> _reply;
    public AuthoredGumpView View { get; private set; }
    protected override bool DirectMouseInput => true;
    protected override float MaxArtWidth => Math.Max(320, _document.Width + 32);
    private ModernAuthoredGump(World world, GumpDocument document, Action<GumpReply> reply) : base(world)
    {
        _document = document; _reply = reply;
    }
    /// <summary>
    /// Explicitly opened by game code. The caller supplies its existing game action/reply handler;
    /// incoming shard gumps and the automatic Modern registry are unaffected.
    /// </summary>
    public static ModernAuthoredGump Show(World world, GumpDocument document, Action<GumpReply> reply)
    {
        document.Validate();
        Current?.Close();
        var host = new ModernAuthoredGump(world, document, reply);
        Client.Game.AddChild(host); host.Open(); return host;
    }
    protected override void Build(PanelContainer card)
    {
        var column = new VBoxContainer(); card.AddChild(column);
        var header = new HBoxContainer(); column.AddChild(header);
        header.AddChild(new Label { Text = _document.Name, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        var close = new Button { Text = "Close" }; header.AddChild(close); close.Pressed += Close;
        var scroll = new ScrollContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill }; column.AddChild(scroll);
        View = new AuthoredGumpView { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        scroll.AddChild(View); View.Build(_document);
        // Keep the design's vertical extent scrollable on smaller screens; modern horizontal anchors reflow.
        View.CustomMinimumSize = new Vector2(_document.Modern ? 240 : _document.Width, _document.Height);
        View.Reply += r => _reply?.Invoke(r);
    }
    public override void Close() { base.Close(); QueueFree(); }
    public static bool HandleDesktopInput(InputEvent input) => Current is ModernAuthoredGump && HandleInput(input);

    public static void PreviewFile(World world, string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0].Equals("gumpopen", StringComparison.OrdinalIgnoreCase)) args = args[1..];
            if (args.Length == 0 || args[0] != Path.GetFileName(args[0]))
                throw new InvalidDataException("Usage: gumpopen filename.gump.json (in user://gump_studio)");
            var doc = GumpDocument.Parse(File.ReadAllText(Path.Combine(GumpCapture.DirectoryPath, args[0])));
            Show(world, doc, r => GameActions.Print(world, $"Preview reply {r.ButtonId}; {r.Switches.Length} switches, {r.Entries.Length} entries. No packet sent."));
        }
        catch (Exception ex) { GameActions.Print(world, "Gump preview: " + ex.Message); }
    }
}
