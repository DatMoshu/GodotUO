#if TOOLS
namespace GUO.Editor;

using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using Godot;

public partial class EditorSmoke
{
    private void HueRangeCheck(string name, bool ok)
    { _assetReport["hue_range_" + name] = ok; if (!ok) AssetFail("Hue range: " + name); }
    private void CheckHueRanges()
    {
        ushort[] input = Enumerable.Range(0, 32).Select(i => (ushort)(0x8000 | i)).ToArray(); input[3] = 0xFFFF;
        ushort[] copy = (ushort[])input.Clone();
        ushort[] spread = HueRange.Transform(input, 0, 3, HueRangeOperation.Spread);
        HueRangeCheck("fiddler_spread_golden", spread.Take(4).SequenceEqual(new ushort[] { 0x8000, 0x3DEF, 0x7FFF, 0xFFFF }));
        HueRangeCheck("outside_range_and_input_unchanged", spread.Skip(4).SequenceEqual(input.Skip(4)) && input.SequenceEqual(copy));
        HueRangeCheck("reversed_bounds_same_gradient", HueRange.Transform(input, 3, 0, HueRangeOperation.Spread).SequenceEqual(spread));
        ushort[] exponential = HueRange.Transform(input, 0, 3, HueRangeOperation.Exponential);
        HueRangeCheck("fiddler_exponential_golden", exponential.Take(4).SequenceEqual(new ushort[] { 0x8000, 1, 0x1084, 0xFFFF }));
        var reversed = HueRange.Transform(input, 3, 1, HueRangeOperation.Reverse);
        HueRangeCheck("reverse_inclusive_preserves_all_bits", reversed[0] == 0x8000 && reversed[1] == 0xFFFF && reversed[2] == 0x8002 && reversed[3] == 0x8001 && reversed.Skip(4).SequenceEqual(input.Skip(4)));
        HueRangeCheck("adjacent_and_single_gradient_noop", HueRange.Transform(input, 1, 2, HueRangeOperation.Spread).SequenceEqual(input)
            && HueRange.Transform(input, 1, 1, HueRangeOperation.Exponential).SequenceEqual(input));
        var dark = new ushort[32];
        HueRangeCheck("nonzero_dark_clamp", HueRange.Transform(dark, 0, 0, HueRangeOperation.ShiftRgb, 1)[0] == 1);
        HueRangeCheck("zero_rgb_stays_zero", HueRange.Transform(dark, 0, 0, HueRangeOperation.ShiftRgb)[0] == 0);
        HueRangeCheck("rgb_shift_clamps_and_writes_15_bits", HueRange.Transform(input, 1, 1, HueRangeOperation.ShiftRgb, 255, -255, -255)[1] == 0x7C00
            && HueRange.Transform(input, 3, 3, HueRangeOperation.ShiftRgb, -255, 255, 255)[3] == 0x03FF);
        var whites = Enumerable.Repeat((ushort)0xFFFF, 32).ToArray();
        HueRangeCheck("reference_exponential_overflow_refused", HueRefused(() => HueRange.Transform(whites, 0, 3, HueRangeOperation.Exponential)) && whites.All(c => c == 0xFFFF));
        HueRangeCheck("bad_range_operation_and_delta_refused", HueRefused(() => HueRange.Transform(input, -1, 3, HueRangeOperation.Spread))
            && HueRefused(() => HueRange.Transform(input, 0, 32, HueRangeOperation.Reverse)) && HueRefused(() => HueRange.Transform(input, 0, 3, (HueRangeOperation)99))
            && HueRefused(() => HueRange.Transform(input, 0, 3, HueRangeOperation.ShiftRgb, 256)));
        // Equal nonzero channels retain the reference's first-interior +1 behavior.
        var equal = Enumerable.Repeat((ushort)0x739C, 32).ToArray();
        HueRangeCheck("equal_channel_reference_quirk", HueRange.Transform(equal, 0, 3, HueRangeOperation.Exponential)[1] == 0x77BD);

        string root = Path.Combine(_out, "hue-ranges" + Suffix + "-" + Guid.NewGuid().ToString("N"));
        string project = Path.Combine(root, "project"), install = Path.Combine(root, "install"); Directory.CreateDirectory(install);
        byte[] source = new byte[88]; for (int i = 0; i < 32; i++) BinaryPrimitives.WriteUInt16LittleEndian(source.AsSpan(i * 2), input[i]);
        BinaryPrimitives.WriteUInt16LittleEndian(source.AsSpan(64), 0xCAFE); BinaryPrimitives.WriteUInt16LittleEndian(source.AsSpan(66), 0xDEAD); Encoding.ASCII.GetBytes("source").CopyTo(source, 68);
        HueDocument Open() => HueDocument.Open(project, install, 1, () => source);
        var document = Open(); document.DraftName = "range batch"; document.DraftStart = "0x1234";
        HueRange.Apply(document, 0, 3, HueRangeOperation.Spread);
        HueRangeCheck("pending_fields_and_operation_atomic", document.Name == "range batch" && document.Start == 0x1234 && document.End == 0xDEAD && document.Colors().SequenceEqual(spread));
        document.Undo(); HueRangeCheck("one_undo_restores_exact_native_record", document.PreviewRecord().SequenceEqual(source)); document.Redo();
        HueRangeCheck("redo_restores_operation", document.Colors().SequenceEqual(spread) && document.Name == "range batch");
        document.DraftColor = "invalid"; ushort[] before = document.Colors();
        HueRangeCheck("invalid_raw_draft_refuses_without_mutation", HueRefused(() => HueRange.Apply(document, 0, 3, HueRangeOperation.Reverse))
            && document.Colors().SequenceEqual(before) && document.DraftColor == "invalid"); document.Undo();
        document.Save(); var reopened = Open();
        HueRangeCheck("save_reload_keeps_operation_and_bounds", reopened.Colors().SequenceEqual(spread) && reopened.Start == 0x1234 && reopened.End == 0xDEAD && !reopened.IsDirty);
        before = reopened.Colors(); source[0] ^= 1;
        HueRangeCheck("source_change_refuses_operation", HueRefused(() => HueRange.Apply(reopened, 0, 31, HueRangeOperation.Reverse)) && reopened.Colors().SequenceEqual(before)); source[0] ^= 1;
        var failed = Open(); failed.ReplacePalette(whites); failed.DraftName = "pending name"; before = failed.Colors();
        HueRangeCheck("overflow_preserves_pending_fields_and_history", HueRefused(() => HueRange.Apply(failed, 0, 3, HueRangeOperation.Exponential))
            && failed.DraftName == "pending name" && failed.Name == "range batch" && failed.Colors().SequenceEqual(before));

        var settings = EditorInterface.Singleton.GetEditorSettings(); Variant previous = settings.GetProjectMetadata("guo_hues", "drafts", "[]"); settings.SetProjectMetadata("guo_hues", "drafts", "[]");
        var editor = new HueEditor(); AddChild(editor);
        try
        {
            editor.Bind(reopened, null); editor.ChooseRange(3, 1, HueRangeOperation.Reverse); before = reopened.Colors();
            HueRangeCheck("native_editor_range_apply", editor.Operations.Visible && !editor.RangeApply.Disabled && editor.ApplyRange()
                && reopened.ColorAt(1) == before[3] && reopened.ColorAt(3) == before[1]);
            editor.Undo(); HueRangeCheck("native_editor_one_undo", reopened.Colors().SequenceEqual(before) && !reopened.IsDirty);
            editor.ChooseRange(0, 0, HueRangeOperation.ShiftRgb, 1); HueRangeCheck("native_rgb_shift_apply", editor.ApplyRange() && reopened.ColorAt(0) == 1); editor.Undo();
            editor.Discard(reopened);
        }
        finally { editor.Free(); settings.SetProjectMetadata("guo_hues", "drafts", previous); }
    }
}
#endif
