#if TOOLS
namespace GUO.Editor;

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;

public partial class EditorSmoke
{
    private Task _multiTextProof;
    private void StepMultiTextProof()
    {
        if (_stage == 10) return;
        if (!_data.IsLoaded)
        {
            if (_data.Error != null || _elapsed > 180) { _failures.Add("Multi TXT proof: client data did not load: " + _data.Error); Finish(); }
            return;
        }
        if (_multiTextProof == null)
        {
            MultiEdit.OnDataLoaded();
            string dir=Path.Combine(_out,"multi-text-proof"); Directory.CreateDirectory(dir);
            _meReport["ok"]=true; _multiTextProof=MeFiddlerTextAsync(MultiEdit,dir);
        }
        if (_multiTextProof.IsCompleted)
        {
            if (!_multiTextProof.IsCompletedSuccessfully) MeCheck("fiddler_txt_proof_completed",false,_multiTextProof.Exception?.GetBaseException().Message ?? "canceled");
            _report["multiedit"]=_meReport; Finish();
        }
        else if (_elapsed > 180) { MeCheck("fiddler_txt_proof_finished_in_time",false); Finish(); }
    }

    private async Task MeFiddlerTextAsync(MultiEditView view, string dir)
    {
        MultiDocument doc = view.Doc;
        string source = Path.Combine(dir,"fiddler-source.txt");
        const string text = "1650 12 -8 7 256\r\n0xA 13 -7 -2 -2147483648\r\n0xA 13 -7 -2 -2147483648\r\n";
        File.WriteAllText(source,text);
        string error = await view.ImportFileAsync(source,"txt-fiddler");
        MeCheck("fiddler_txt_hex_coordinates_raw_flags", error == null && doc.Parts.Count == 3 && doc.Parts[0].Id == 0x1650 && doc.Parts[0].X == 12 && doc.Parts[0].Y == -8 && doc.Parts[1].TextFlags == int.MinValue,error);
        string json=MultiStore.ToJson(doc.Name,doc.Source,doc.Parts);
        var restored=MultiStore.FromJson(json);
        MeCheck("fiddler_txt_project_flags_order_duplicates", restored.Parts.Count == 3 && restored.Parts.Select(p=>p.TextFlags).SequenceEqual(new int?[]{256,int.MinValue,int.MinValue}) && restored.Parts[1].SameAs(restored.Parts[2]));
        MultiTextSnapshot stale=new(doc);
        doc.SetShown(new[]{doc.Parts[0].Uid},false);
        MeCheck("fiddler_txt_visibility_override", doc.Parts[0].ExportTextFlags == 0 && doc.Parts[0].TextFlags == 256);
        doc.Undo(); MeCheck("fiddler_txt_undo_restores_flags",doc.Parts[0].ExportTextFlags == 256);
        bool refused=false; try { stale.Check(doc); } catch(IOException) { refused=true; }
        MeCheck("fiddler_txt_undo_invalidates_review",refused);
        string exported=Path.Combine(dir,"fiddler-export.txt");
        error=await view.ExportFileAsync(exported,"txt-fiddler");
        MeCheck("fiddler_txt_export_keeps_all_scalar_fields",error == null && File.ReadAllText(exported)==text.Replace("1650 ","0x1650 "),error);
        string lossy=Path.Combine(dir,"opaque-legacy-refused.txt");
        MeCheck("fiddler_txt_other_format_flag_loss_refused",await view.ExportFileAsync(lossy,"txt") != null && !File.Exists(lossy));
        doc.Selection.Add(doc.Parts[0].Uid); view.CopySelection(); view.PasteClipboard();
        view.Canvas.PlaceGhostAt(0,0);
        MeCheck("fiddler_txt_copy_paste_flags",doc.Parts[^1].TextFlags == 256);
        doc.Undo(); doc.Selection.Clear();
        await view.TransformAsync("rotate",1);
        MeCheck("fiddler_txt_transform_flags",doc.Parts.Select(p=>p.TextFlags).SequenceEqual(new int?[]{256,int.MinValue,int.MinValue}));
        doc.Undo();
        SaveResult native=await MultiStore.SaveAsync("opaque-flags-refused",doc.Parts,_data.Files.TileData.StaticData,Path.Combine(dir,"opaque-native-refused"));
        MeCheck("fiddler_txt_native_refuses_opaque_flags",!native.Ok && native.Error?.Contains("opaque Fiddler") == true && !Directory.Exists(Path.Combine(dir,"opaque-native-refused")),native.Error);

        string mutable=Path.Combine(dir,"mutable-input.txt"); File.WriteAllText(mutable,text);
        MultiTextInput input=new(mutable); File.AppendAllText(mutable,"0x1 0 0 0 1\r\n");
        refused=false; try { input.Check(); } catch(IOException) { refused=true; }
        MeCheck("fiddler_txt_source_change_refused",refused);
        string target=Path.Combine(dir,"mutable-output.txt"); File.WriteAllText(target,"before");
        MultiTextDestination destination=new(target); File.WriteAllText(target,"changed externally");
        refused=false; try { destination.Write(new byte[]{1,2}); } catch(IOException) { refused=true; }
        MeCheck("fiddler_txt_destination_change_refused",refused && File.ReadAllText(target)=="changed externally");
        refused=false; try { new MultiTextDestination(source); } catch(IOException) { refused=true; }
        MeCheck("fiddler_txt_source_overwrite_refused",refused);
        MultiPart[] centered=MultiTextSnapshot.Centre(doc.Parts.ToArray());
        MeCheck("fiddler_txt_optional_centre_only_xy",centered[0].X==0 && centered[0].Y==0 && centered[0].Z==7 && centered[0].TextFlags==256);

        await view.ReviewFiddlerTextAsync(source,false);
        MeCheck("fiddler_txt_import_review_preserves_coordinates_default",view.TextReview != null && view.TextReview.FindChildren("*","CheckBox",true,false).OfType<CheckBox>().Single().ButtonPressed == false);
        if (!Headless)
        {
            EditorInterface.Singleton.SetMainScreenEditor(MultiEditView.TabName);
            foreach (int width in new[]{1024,1366,1920})
            {
                GetWindow().Mode=Window.ModeEnum.Windowed;
                GetWindow().Size=(Vector2I)(new Vector2(width,width==1920?1080:768)*EditorInterface.Singleton.GetEditorScale());
                view.TextReview.PopupCenteredClamped(); await Frames(12);
                using Image image=view.TextReview.GetTexture()?.GetImage();
                image?.SavePng(Path.Combine(_out,$"multi-fiddler-txt-import-{width}.png"));
                MeCheck($"fiddler_txt_review_fits_{width}",image != null && view.TextReview.Size.X <= GetWindow().Size.X && view.TextReview.Size.Y <= GetWindow().Size.Y);
            }
        }
        view.TextReview.EmitSignal(ConfirmationDialog.SignalName.Canceled); await Frames(2);
        MeCheck("fiddler_txt_cancel_preserves_document",doc.Parts[0].X==12 && doc.Parts[0].TextFlags==256);
        doc.SetHue(new[]{doc.Parts[0].Uid},21);
        string hued=Path.Combine(dir,"hue-loss-reviewed.txt");
        MeCheck("fiddler_txt_unreviewed_hue_loss_refused",await view.ExportFileAsync(hued,"txt-fiddler") != null && !File.Exists(hued));
        await view.ReviewFiddlerTextAsync(hued,true);
        ConfirmationDialog review=view.TextReview;
        MeCheck("fiddler_txt_hue_loss_confirmation_required",review != null && review.GetOkButton().Disabled);
        if (!Headless)
        {
            await Frames(12); using Image image=review.GetTexture()?.GetImage();
            image?.SavePng(Path.Combine(_out,"multi-fiddler-txt-hue-loss.png"));
        }
        CheckBox loss=review.FindChildren("*","CheckBox",true,false).OfType<CheckBox>().Single();
        loss.ButtonPressed=true;
        review.EmitSignal(ConfirmationDialog.SignalName.Confirmed); await Frames(2);
        MeCheck("fiddler_txt_reviewed_export_keeps_working_hue",File.Exists(hued) && doc.Parts[0].Hue==21 && doc.Parts[0].TextFlags==256);
        view.NewMulti();
        await MeFiddlerUoaAsync(view,dir);
    }

    private async Task MeFiddlerUoaAsync(MultiEditView view,string dir)
    {
        MultiDocument doc=view.Doc;
        string source=Path.Combine(dir,"fiddler-source.uoa");
        const string text="77 version\r\n98 template id\r\n33 item version\r\n2 num components\r\n1650 12 -8 7 256\r\n1650 13 -7 -2 -2147483648\r\n";
        File.WriteAllText(source,text);
        string error=await view.ImportFileAsync(source,"uoa-fiddler");
        MeCheck("fiddler_uoa_decimal_ids_coordinates_flags",error==null && doc.Parts.Count==2 && doc.Parts[0].Id==1650 && doc.Parts[0].X==12 && doc.Parts[1].TextFlags==int.MinValue,error);
        string output=Path.Combine(dir,"fiddler-export.uoa");
        error=await view.ExportFileAsync(output,"uoa-fiddler");
        MeCheck("fiddler_uoa_canonical_header_and_exact_records",error==null && File.ReadAllText(output)==text.Replace("77 version","6 version").Replace("98 template id","1 template id").Replace("33 item version","-1 item version"),error);
        var restored=MultiStore.FromJson(MultiStore.ToJson(doc.Name,doc.Source,doc.Parts));
        MeCheck("fiddler_uoa_project_keeps_raw_flags",restored.Parts[0].TextFlags==256 && restored.Parts[1].TextFlags==int.MinValue);
        await view.ReviewFiddlerTextAsync(source,false,"uoa-fiddler");
        ConfirmationDialog review=view.TextReview;
        MeCheck("fiddler_uoa_review_has_correct_format",review!=null && review.Title.Contains("UOA"));
        Tree rows=review.FindChildren("*","Tree",true,false).OfType<Tree>().Single();
        MeCheck("fiddler_uoa_review_ids_are_decimal",rows.GetRoot().GetFirstChild().GetText(0)=="1650");
        CheckBox centre=review.FindChildren("*","CheckBox",true,false).OfType<CheckBox>().Single();
        MeCheck("fiddler_uoa_preserves_coordinates_default",!centre.ButtonPressed);
        if (!Headless)
        {
            foreach(int width in new[]{1024,1366,1920})
            {
                GetWindow().Size=(Vector2I)(new Vector2(width,width==1920?1080:768)*EditorInterface.Singleton.GetEditorScale());
                review.PopupCenteredClamped(); await Frames(12);
                using Image image=review.GetTexture()?.GetImage(); image?.SavePng(Path.Combine(_out,$"multi-fiddler-uoa-import-{width}.png"));
                MeCheck($"fiddler_uoa_review_fits_{width}",image!=null && review.Size.X<=GetWindow().Size.X && review.Size.Y<=GetWindow().Size.Y);
            }
        }
        centre.ButtonPressed=true; review.EmitSignal(ConfirmationDialog.SignalName.Confirmed); await Frames(2);
        MeCheck("fiddler_uoa_review_centres_only_xy",doc.Parts[0].X==0 && doc.Parts[0].Y==0 && doc.Parts[0].Z==7 && doc.Parts[0].TextFlags==256);
        doc.SetHue(new[]{doc.Parts[0].Uid},21);
        string hued=Path.Combine(dir,"reviewed-hue-loss.uoa");
        MeCheck("fiddler_uoa_unreviewed_hue_loss_refused",await view.ExportFileAsync(hued,"uoa-fiddler")!=null && !File.Exists(hued));
        await view.ReviewFiddlerTextAsync(hued,true,"uoa-fiddler"); review=view.TextReview;
        MeCheck("fiddler_uoa_hue_loss_requires_consent",review.Title.Contains("UOA") && review.GetOkButton().Disabled);
        if(!Headless) { await Frames(12); using Image image=review.GetTexture()?.GetImage(); image?.SavePng(Path.Combine(_out,"multi-fiddler-uoa-hue-loss.png")); }
        review.FindChildren("*","CheckBox",true,false).OfType<CheckBox>().Single().ButtonPressed=true;
        review.EmitSignal(ConfirmationDialog.SignalName.Confirmed); await Frames(2);
        MeCheck("fiddler_uoa_reviewed_export_keeps_document",File.Exists(hued) && File.ReadAllText(hued).EndsWith("1650 1 1 -2 -2147483648\r\n") && doc.Parts[0].Hue==21);
        view.NewMulti();
    }
}
#endif
