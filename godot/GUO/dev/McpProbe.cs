// SPDX-License-Identifier: BSD-2-Clause
using Godot;

namespace GUO.Dev;

/// <summary>Synthetic MCP integration fixture; no UO files or shard required.</summary>
public partial class McpProbe : Node
{
    public override void _Ready()
    {
        var button = new Button { Text = "Click me", Position = new Vector2(20, 20), Size = new Vector2(180, 50) };
        var status = new Label { Text = "Waiting", Position = new Vector2(20, 150) };
        var edit = new LineEdit { Position = new Vector2(20, 90), Size = new Vector2(240, 45) };
        button.Pressed += () => status.Text = "Clicked";
        edit.TextChanged += value => status.Text = "Typed: " + value;
        AddChild(button);
        AddChild(edit);
        AddChild(status);
        Automation.McpHost.Attach(this);
    }
}
