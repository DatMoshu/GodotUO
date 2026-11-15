#if TOOLS
namespace GUO.Editor;

using Godot;

/// <summary>Long tool arguments scroll independently of the native approval buttons.</summary>
internal static class AiToolApprovalDialog
{
    internal static ConfirmationDialog Create(string request)
    {
        var dialog = new ConfirmationDialog
        {
            Title = "A model wants to change something",
            OkButtonText = "Allow once",
            CancelButtonText = "Refuse",
            Exclusive = true,
        };
        // AcceptDialog lays custom content above its own button row. Do not put
        // the request in DialogText: its label's minimum height can exceed the screen.
        dialog.AddChild(new RichTextLabel
        {
            Name = "RequestText",
            Text = request,
            BbcodeEnabled = false,
            FitContent = false,
            ScrollActive = true,
            SelectionEnabled = true,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        });
        return dialog;
    }

    internal static void Popup(ConfirmationDialog dialog, float editorScale)
    {
        // Clamp to the parent viewport; large editor scales or small windows
        // reduce the scrolling region rather than pushing the footer off-screen.
        Vector2 available = dialog.GetParent().GetViewport().GetVisibleRect().Size;
        var size = new Vector2I(
            Mathf.Max(1, (int)Mathf.Min(640 * editorScale, available.X * 0.8f)),
            Mathf.Max(1, (int)Mathf.Min(480 * editorScale, available.Y * 0.8f)));
        dialog.MinSize = size;
        dialog.PopupCentered(size);
    }
}
#endif
