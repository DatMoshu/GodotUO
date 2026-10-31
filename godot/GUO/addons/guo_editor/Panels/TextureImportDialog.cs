#if TOOLS
namespace GUO.Editor;

using System;
using System.IO;
using Godot;

/// <summary>Captured native-quantized PNG review; confirmation changes one working document.</summary>
[Tool]
public partial class TextureImportDialog : ConfirmationDialog
{
    internal TextureDocument Document { get; private set; }
    internal static TextureImportDialog Open(TexturePanel panel, TextureDocument document, byte[] png, string name)
    {
        byte[] record = TextureDocument.DecodePng(png); string token = document.ReviewToken;
        var dialog = new TextureImportDialog { Document = document, Title = $"Replace texture 0x{document.Id:X4}",
            OkButtonText = "Replace working texture", DialogHideOnOk = false };
        var body = new VBoxContainer(); body.AddThemeConstantOverride("separation", 12); dialog.AddChild(body);
        body.AddChild(AssetUi.Label(Path.GetFileName(name)));
        var comparison = new GridContainer { Columns = 2, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; body.AddChild(comparison);
        comparison.AddChild(new Label { Text = $"Current · {document.Size} × {document.Size}" });
        int size = TextureDocument.Dimension(record); comparison.AddChild(new Label { Text = $"Replacement · {size} × {size}" });
        foreach (byte[] bytes in new[] { document.Record(), record })
        {
            using Image image = TextureDocument.Image(bytes);
            comparison.AddChild(new TextureRect { Texture = ImageTexture.CreateFromImage(image), TextureFilter = Control.TextureFilterEnum.Nearest,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                CustomMinimumSize = new Vector2(160, 160) * EditorInterface.Singleton.GetEditorScale(), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        }
        body.AddChild(AssetUi.Label("RGB is reduced to 5 bits per channel. Alpha is ignored; every texture pixel, including black, is opaque. No resizing is performed."));
        body.AddChild(AssetUi.Label("Replace changes this working document and is undoable. Save copy persists it in the project; native files stay unchanged."));
        var status = AssetUi.Label(""); status.Visible = false; body.AddChild(status);
        dialog.Confirmed += () =>
        {
            try
            {
                if (!panel.IsCurrentProject(document)) throw new IOException("The project changed. Reopen texture replacement in the active project.");
                document.Import(record, token); panel.OnAssetsChanged(); dialog.QueueFree();
            }
            catch (Exception e) { status.Text = e.Message; status.Visible = true; }
        };
        dialog.Canceled += dialog.QueueFree;
        panel.AddChild(dialog); dialog.PopupCenteredClamped((Vector2I)(new Vector2(640, 450) * EditorInterface.Singleton.GetEditorScale()), 0.9f); return dialog;
    }
}
#endif
