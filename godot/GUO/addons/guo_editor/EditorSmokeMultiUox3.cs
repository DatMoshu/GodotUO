#if TOOLS
namespace GUO.Editor;

using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;

public partial class EditorSmoke
{
    private async Task MeFiddlerUox3Async(MultiEditView view,string dir)
    {
        MultiDocument doc=view.Doc;
        view.OpenParts("uox3-review",new[]{doc.Make(0xabcd,12,-8,7,false,21,textFlags:256),doc.Make(0xabcd,12,-8,7,false,0,textFlags:0)});
        string output=Path.Combine(dir,"fiddler-uox3.dfn");
        MeCheck("fiddler_uox3_direct_loss_refused",await view.ExportFileAsync(output,"uox3-fiddler")!=null && !File.Exists(output));
        MeCheck("fiddler_uox3_import_refused",await view.ImportFileAsync(output,"uox3-fiddler")!=null && doc.Parts.Count==2);
        await view.ReviewFiddlerTextAsync(output,true,"uox3-fiddler"); ConfirmationDialog review=view.TextReview;
        MeCheck("fiddler_uox3_review_requires_loss_consent",review!=null && review.Title.Contains("UOX3") && review.GetOkButton().Disabled);
        if(!Headless)
        {
            foreach(int width in new[]{1024,1366,1920})
            {
                GetWindow().Size=(Vector2I)(new Vector2(width,width==1920?1080:768)*EditorInterface.Singleton.GetEditorScale());
                review.PopupCenteredClamped(); await Frames(12); using Image image=review.GetTexture()?.GetImage(); image?.SavePng(Path.Combine(_out,$"multi-fiddler-uox3-export-{width}.png"));
                MeCheck($"fiddler_uox3_review_fits_{width}",image!=null && review.Size.X<=GetWindow().Size.X && review.Size.Y<=GetWindow().Size.Y);
            }
        }
        review.FindChildren("*","CheckBox",true,false).OfType<CheckBox>().Single().ButtonPressed=true;
        review.EmitSignal(ConfirmationDialog.SignalName.Confirmed); await Frames(2);
        string expected=string.Concat(Enumerable.Range(0,2).Select(i=>$"[HOUSE ITEM {i}]\r\n{{\r\nITEM=0xABCD\r\nX=12\r\nY=-8\r\nZ=7\r\n}}\r\n\r\n"));
        MeCheck("fiddler_uox3_exact_export_working_fields_retained",File.Exists(output) && File.ReadAllText(output)==expected && doc.Parts[0].Hue==21 && doc.Parts[0].TextFlags==256 && !doc.Parts[0].Shown);
        string canceled=Path.Combine(dir,"uox3-cancel.dfn"); await view.ReviewFiddlerTextAsync(canceled,true,"uox3-fiddler"); review=view.TextReview;
        review.EmitSignal(ConfirmationDialog.SignalName.Canceled); await Frames(2);
        MeCheck("fiddler_uox3_cancel_no_output",!File.Exists(canceled));
        string stale=Path.Combine(dir,"uox3-stale.dfn"); await view.ReviewFiddlerTextAsync(stale,true,"uox3-fiddler"); review=view.TextReview;
        review.FindChildren("*","CheckBox",true,false).OfType<CheckBox>().Single().ButtonPressed=true;
        doc.SetHue(new[]{doc.Parts[0].Uid},22); review.EmitSignal(ConfirmationDialog.SignalName.Confirmed); await Frames(2);
        MeCheck("fiddler_uox3_stale_document_refused",!File.Exists(stale)); review.EmitSignal(ConfirmationDialog.SignalName.Canceled); await Frames(2);
        MultiPart extra=doc.Parts[0];extra.TextExtra=1;view.OpenParts("uox3-extra",new[]{extra});
        MeCheck("fiddler_uox3_extra_loss_refused",await view.ExportFileAsync(Path.Combine(dir,"uox3-extra.dfn"),"uox3-fiddler")!=null && !File.Exists(Path.Combine(dir,"uox3-extra.dfn")));
        view.NewMulti();
    }
}
#endif
