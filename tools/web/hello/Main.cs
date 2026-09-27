using System.Runtime.InteropServices;
using Godot;

public partial class Main : Node2D
{
    public override void _Ready()
    {
        string runtime = $"{RuntimeInformation.FrameworkDescription}, {RuntimeInformation.OSDescription}";
        GD.Print($"[GUO] web hello from C#: {runtime}, 2 + 2 = {2 + 2}");
        GetNode<Label>("Label").Text = "GUO web hello from C#\n" + runtime;
    }

    public override void _Draw()
    {
        DrawRect(new Rect2(40, 140, 200, 120), new Color(0.8f, 0.2f, 0.2f));
    }
}
