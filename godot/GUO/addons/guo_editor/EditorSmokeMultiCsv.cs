#if TOOLS
namespace GUO.Editor;

using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;

public partial class EditorSmoke
{
    private async Task MeFiddlerCsvAsync(MultiEditView view,string dir)
    {
        MultiDocument doc=view.Doc; string source=Path.Combine(dir,"fiddler-source.csv");
        const string header="TileID,OffsetX,OffsetY,OffsetZ,Flag,Unk1\r\n";
        const string input=header+"1650,12,-8,7,100,0x100\r\n0x000a,13,-7,-2,80000000,-2147483648\r\n0x000a,13,-7,-2,80000000,-2147483648\r\n";
        const string canonical=header+"0x1650,12,-8,7,0x100,0x100\r\n0x000a,13,-7,-2,0x80000000,0x80000000\r\n0x000a,13,-7,-2,0x80000000,0x80000000\r\n";
        File.WriteAllText(source,input); string error=await view.ImportFileAsync(source,"csv-fiddler");
        MeCheck("fiddler_csv_hex_flags_extra_coordinates",error==null && doc.Parts.Count==3 && doc.Parts[0].Id==0x1650 && doc.Parts[0].X==12 && doc.Parts[0].TextFlags==256 && doc.Parts[0].TextExtra==256 && doc.Parts[1].TextExtra==int.MinValue,error);
        var restored=MultiStore.FromJson(MultiStore.ToJson(doc.Name,doc.Source,doc.Parts));
        MeCheck("fiddler_csv_project_retains_extra_duplicates",restored.Parts.Select(p=>p.TextExtra).SequenceEqual(new int?[]{256,int.MinValue,int.MinValue}) && restored.Parts[1].SameAs(restored.Parts[2]));
        MultiPart nullable=doc.Parts[0]; nullable.TextFlags=null;
        MeCheck("fiddler_csv_null_seventh_placeholder",MultiStore.FromJson(MultiStore.ToJson("null-flags",null,new[]{nullable})).Parts[0].TextFlags==null && MultiStore.FromJson(MultiStore.ToJson("null-flags",null,new[]{nullable})).Parts[0].TextExtra==256);
        MultiTextSnapshot stale=new(doc);
        doc.Do("edit CSV extra",list=>{ MultiPart p=list[0]; p.TextExtra=1024; list[0]=p; });
        MeCheck("fiddler_csv_extra_edit_is_history",doc.Parts[0].TextExtra==1024 && doc.CanUndo);
        doc.Undo(); bool refused=false; try { stale.Check(doc); } catch(IOException) { refused=true; }
        MeCheck("fiddler_csv_undo_restores_extra_invalidates_review",doc.Parts[0].TextExtra==256 && refused);
        string output=Path.Combine(dir,"fiddler-export.csv"); error=await view.ExportFileAsync(output,"csv-fiddler");
        MeCheck("fiddler_csv_exact_scalar_export",error==null && File.ReadAllText(output)==canonical,error);
        MeCheck("fiddler_csv_cross_format_loss_refused",await view.ExportFileAsync(Path.Combine(dir,"extra-loss.txt"),"txt-fiddler")!=null && !File.Exists(Path.Combine(dir,"extra-loss.txt")));
        MultiPart nativePart=doc.Parts[0]; nativePart.TextFlags=1; nativePart.Shown=true;
        SaveResult native=await MultiStore.SaveAsync("csv-extra-refused",new[]{nativePart},_data.Files.TileData.StaticData,Path.Combine(dir,"csv-native-refused"));
        MeCheck("fiddler_csv_native_refuses_extra_alone",!native.Ok && native.Error.Contains("Unk1") && !Directory.Exists(Path.Combine(dir,"csv-native-refused")));
        doc.Selection.Add(doc.Parts[0].Uid); view.CopySelection(); view.PasteClipboard(); view.Canvas.PlaceGhostAt(0,0);
        MeCheck("fiddler_csv_copy_paste_keeps_extra",doc.Parts[^1].TextExtra==256); doc.Undo(); doc.Selection.Clear(); doc.Selection.Add(doc.Parts[0].Uid);
        string oldStamps=view.StampsDir; view.StampsDir=Path.Combine(dir,"csv-stamps"); view.SaveStamp("csv-extra"); view.PlaceStamp("csv-extra"); view.Canvas.PlaceGhostAt(1,1);
        MeCheck("fiddler_csv_stamp_keeps_extra",doc.Parts[^1].TextExtra==256); doc.Undo(); doc.Selection.Clear(); view.StampsDir=oldStamps;
        await view.TransformAsync("rotate",1);
        MeCheck("fiddler_csv_transform_keeps_extra_order",doc.Parts.Select(p=>p.TextExtra).SequenceEqual(new int?[]{256,int.MinValue,int.MinValue})); doc.Undo();
        await view.ReviewFiddlerTextAsync(source,false,"csv-fiddler"); ConfirmationDialog review=view.TextReview;
        Tree rows=review.FindChildren("*","Tree",true,false).OfType<Tree>().Single();
        MeCheck("fiddler_csv_review_extra_column",review.Title.Contains("CSV") && rows.Columns==6 && rows.GetRoot().GetFirstChild().GetText(5)=="256");
        CheckBox centre=review.FindChildren("*","CheckBox",true,false).OfType<CheckBox>().Single();
        MeCheck("fiddler_csv_default_coordinates_preserved",!centre.ButtonPressed);
        if(!Headless)
        {
            foreach(int width in new[]{1024,1366,1920})
            {
                GetWindow().Size=(Vector2I)(new Vector2(width,width==1920?1080:768)*EditorInterface.Singleton.GetEditorScale());
                review.PopupCenteredClamped(); await Frames(12); using Image image=review.GetTexture()?.GetImage(); image?.SavePng(Path.Combine(_out,$"multi-fiddler-csv-import-{width}.png"));
                MeCheck($"fiddler_csv_review_fits_{width}",image!=null && review.Size.X<=GetWindow().Size.X && review.Size.Y<=GetWindow().Size.Y);
            }
        }
        centre.ButtonPressed=true; review.EmitSignal(ConfirmationDialog.SignalName.Confirmed); await Frames(2);
        MeCheck("fiddler_csv_center_retains_extra_flags_z",doc.Parts[0].X==0 && doc.Parts[0].Y==0 && doc.Parts[0].Z==7 && doc.Parts[0].TextFlags==256 && doc.Parts[0].TextExtra==256);
        doc.SetHue(new[]{doc.Parts[0].Uid},21); string hued=Path.Combine(dir,"reviewed-hue-loss.csv");
        MeCheck("fiddler_csv_unreviewed_hue_loss_refused",await view.ExportFileAsync(hued,"csv-fiddler")!=null && !File.Exists(hued));
        await view.ReviewFiddlerTextAsync(hued,true,"csv-fiddler"); review=view.TextReview;
        MeCheck("fiddler_csv_hue_loss_requires_consent",review.GetOkButton().Disabled);
        if(!Headless)
        {
            foreach(int width in new[]{1024,1366,1920})
            {
                GetWindow().Size=(Vector2I)(new Vector2(width,width==1920?1080:768)*EditorInterface.Singleton.GetEditorScale());
                review.PopupCenteredClamped(); await Frames(12); using Image image=review.GetTexture()?.GetImage(); image?.SavePng(Path.Combine(_out,$"multi-fiddler-csv-hue-loss-{width}.png"));
                MeCheck($"fiddler_csv_hue_review_fits_{width}",image!=null && review.Size.X<=GetWindow().Size.X && review.Size.Y<=GetWindow().Size.Y);
            }
        }
        review.FindChildren("*","CheckBox",true,false).OfType<CheckBox>().Single().ButtonPressed=true; review.EmitSignal(ConfirmationDialog.SignalName.Confirmed); await Frames(2);
        MeCheck("fiddler_csv_reviewed_export_keeps_extra_and_working_hue",File.Exists(hued) && File.ReadAllText(hued).Contains("0x100,0x100") && doc.Parts[0].Hue==21 && doc.Parts[1].TextExtra==int.MinValue);
        view.NewMulti();
        await MeFiddlerUox3Async(view,dir);
    }
}
#endif
