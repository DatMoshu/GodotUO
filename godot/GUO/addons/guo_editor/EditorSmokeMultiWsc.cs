#if TOOLS
namespace GUO.Editor;

using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;

public partial class EditorSmoke
{
    private async Task MeFiddlerWscAsync(MultiEditView view,string dir)
    {
        string source=Path.Combine(dir,"fiddler-source.wsc");
        string Record(int n,int id,int x,int y,int z) => $"SECTION WORLDITEM {n}\r\n{{\r\n\tID\t{id}\r\n\tX\t{x}\r\n\tY\t{y}\r\n\tZ\t{z}\r\n\tColor\t21\r\n}}\r\n";
        string text=Record(0,1650,120,80,7)+Record(1,42,123,82,-2);
        File.WriteAllText(source,text);
        string error=await view.ImportFileAsync(source,"wsc-fiddler"); MultiDocument doc=view.Doc;
        MeCheck("fiddler_wsc_decimal_world_heuristic",error==null && doc.Parts.Count==2 && doc.Parts[0].Id==1650 && doc.Parts[0].X==0 && doc.Parts[1].X==3 && doc.Parts[1].Y==2 && doc.Parts[1].Z==-2,error);
        MeCheck("fiddler_wsc_color_ignored_default_visible",doc.Parts.All(p=>p.Hue==0 && p.Shown && p.TextFlags==1));
        string output=Path.Combine(dir,"fiddler-export.wsc"); error=await view.ExportFileAsync(output,"wsc-fiddler");
        MeCheck("fiddler_wsc_exact_export",error==null && File.ReadAllText(output)==(Record(0,1650,0,0,7)+Record(1,42,3,2,-2)).Replace("Color\t21","Color\t0"),error);
        await view.ReviewFiddlerTextAsync(source,false,"wsc-fiddler"); ConfirmationDialog review=view.TextReview;
        MeCheck("fiddler_wsc_review_format",review!=null && review.Title.Contains("WSC"));
        LineEdit dynamic=review.FindChildren("*","LineEdit",true,false).OfType<LineEdit>().Single(e=>e.Name=="WscDynamicIds");
        Tree rows=review.FindChildren("*","Tree",true,false).OfType<Tree>().Single();
        MeCheck("fiddler_wsc_empty_dynamic_set_default",dynamic.Text=="" && rows.GetRoot().GetFirstChild().GetText(4)=="1");
        dynamic.Text="0x672"; dynamic.EmitSignal(LineEdit.SignalName.TextChanged,dynamic.Text);
        MeCheck("fiddler_wsc_invalid_dynamic_ids_block_apply",review.GetOkButton().Disabled);
        dynamic.Text="1650"; dynamic.EmitSignal(LineEdit.SignalName.TextChanged,dynamic.Text);
        MeCheck("fiddler_wsc_dynamic_preview",!review.GetOkButton().Disabled && rows.GetRoot().GetFirstChild().GetText(0)=="1650" && rows.GetRoot().GetFirstChild().GetText(4)=="0");
        if(!Headless)
        {
            foreach(int width in new[]{1024,1366,1920})
            {
                GetWindow().Size=(Vector2I)(new Vector2(width,width==1920?1080:768)*EditorInterface.Singleton.GetEditorScale());
                review.PopupCenteredClamped(); await Frames(12); using Image image=review.GetTexture()?.GetImage();
                image?.SavePng(Path.Combine(_out,$"multi-fiddler-wsc-import-{width}.png"));
                MeCheck($"fiddler_wsc_review_fits_{width}",image!=null && review.Size.X<=GetWindow().Size.X && review.Size.Y<=GetWindow().Size.Y);
            }
        }
        review.EmitSignal(ConfirmationDialog.SignalName.Confirmed); await Frames(2);
        MeCheck("fiddler_wsc_dynamic_apply",!doc.Parts[0].Shown && doc.Parts[0].TextFlags==0 && doc.Parts[1].Shown && doc.Parts[0].X==0);
        MultiPart[] parts=doc.Parts.ToArray(); parts[0].Hue=21; parts[1].TextFlags=256; doc.Reset(parts,"wsc-loss");
        string lossy=Path.Combine(dir,"reviewed-field-loss.wsc");
        MeCheck("fiddler_wsc_unreviewed_field_loss_refused",await view.ExportFileAsync(lossy,"wsc-fiddler")!=null && !File.Exists(lossy));
        await view.ReviewFiddlerTextAsync(lossy,true,"wsc-fiddler"); review=view.TextReview;
        MeCheck("fiddler_wsc_field_loss_requires_consent",review.GetOkButton().Disabled);
        if(!Headless) { await Frames(12); using Image image=review.GetTexture()?.GetImage(); image?.SavePng(Path.Combine(_out,"multi-fiddler-wsc-field-loss.png")); }
        review.FindChildren("*","CheckBox",true,false).OfType<CheckBox>().Single().ButtonPressed=true;
        review.EmitSignal(ConfirmationDialog.SignalName.Confirmed); await Frames(2);
        MeCheck("fiddler_wsc_reviewed_export_keeps_working_fields",File.Exists(lossy) && !doc.Parts[0].Shown && doc.Parts[0].Hue==21 && doc.Parts[1].TextFlags==256 && File.ReadAllText(lossy).Contains("Color\t0"));
        var restored=MultiStore.FromJson(MultiStore.ToJson(doc.Name,doc.Source,doc.Parts));
        MeCheck("fiddler_wsc_project_keeps_visibility",!restored.Parts[0].Shown && restored.Parts[0].TextFlags==0 && restored.Parts[1].TextFlags==256);
        await view.ReviewFiddlerTextAsync(source,false,"wsc-fiddler"); review=view.TextReview;
        review.EmitSignal(ConfirmationDialog.SignalName.Canceled); await Frames(2);
        MeCheck("fiddler_wsc_cancel_keeps_working_copy",doc.Parts[0].Hue==21 && !doc.Parts[0].Shown);
        File.WriteAllText(source,Record(0,65535,0,0,0));
        MeCheck("fiddler_wsc_sentinel_import_refused",await view.ImportFileAsync(source,"wsc-fiddler")!=null && doc.Parts[0].Hue==21);
        view.NewMulti();
    }
}
#endif
