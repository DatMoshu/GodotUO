#if TOOLS
namespace GUO.Editor;

using System.IO;
using System.Text.Json.Nodes;
using Godot;

public partial class EditorSmoke
{
    private void BatchCheck(string name, bool ok)
    { _assetReport["batch_static_" + name] = ok; if (!ok) AssetFail("Batch static: " + name); }

    private JsonNode BatchPreviewFixture()
    {
        string folder = Path.Combine(_out, "batch-pngs"); Directory.CreateDirectory(folder);
        using Image image = FixtureStaticImage();
        image.SavePng(Path.Combine(folder, "0x001A.png"));
        image.SavePng(Path.Combine(folder, "static_0x0E75.png"));
        return new JsonObject
        {
            ["schema"] = 1, ["folder"] = folder, ["ready"] = true, ["count"] = 2, ["ignored"] = 1, ["errors"] = new JsonArray(),
            ["rows"] = new JsonArray(
                new JsonObject { ["file"] = "0x001A.png", ["path"] = Path.Combine(folder, "0x001A.png"), ["id"] = 26, ["width"] = 30, ["height"] = 40, ["error"] = "" },
                new JsonObject { ["file"] = "static_0x0E75.png", ["path"] = Path.Combine(folder, "static_0x0E75.png"), ["id"] = 3701, ["width"] = 30, ["height"] = 40, ["error"] = "" }),
        };
    }

    private void CheckBatchStaticReview()
    {
        var dialog = BatchStaticDialog.Open(_data);
        BatchCheck("initially_requires_scan", !dialog.ReadyToPrepare && dialog.GetOkButton().Disabled);
        JsonNode preview = BatchPreviewFixture(); dialog.ShowScan(preview);
        BatchCheck("valid_preview_lists_all_ids", dialog.ReadyToPrepare && !dialog.GetOkButton().Disabled
            && dialog.Rows.GetRoot().GetChildCount() == 2);
        preview["ready"] = false; preview["errors"] = new JsonArray("Duplicate ID 0x001A");
        preview["rows"][1]["error"] = "Duplicate ID 0x001A"; dialog.ShowScan(preview);
        BatchCheck("errors_block_prepare", !dialog.ReadyToPrepare && dialog.GetOkButton().Disabled);
        dialog.Invalidate();
        BatchCheck("path_change_invalidates_preview", dialog.Rows.GetRoot() == null && dialog.GetOkButton().Disabled);
        dialog.Free();
        BatchCheck("close_does_not_create_stage", !Directory.Exists(Path.Combine(_data.Assets.Root, "staged-static-art")));
    }
}
#endif
