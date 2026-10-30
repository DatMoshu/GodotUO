// SPDX-License-Identifier: BSD-2-Clause
// Synthetic alpha/contact regression; native scene acceptance is a separate gate.
using System;
using System.Collections.Generic;
using System.Reflection;
using GUO.Renderer.DepthWorld;
using Godot;

namespace GUO.Renderer.SpeedMode.Shadows;

public partial class NativeStaticShadowProbe : Node
{
    private static void Check(bool value,string reason)
    { if(!value) throw new InvalidOperationException(reason); }

    public override void _Ready()
    {
        try {
            using var image=Image.CreateEmpty(16,16,false,Image.Format.Rgba8);
            image.Fill(Colors.Transparent);
            // Region [4,12)x[2,14); bottom two rows transparent, opaque foot asymmetric.
            for(int y=2;y<12;y++) for(int x=7;x<10;x++) image.SetPixel(x,y,Colors.White);
            using var texture=ImageTexture.CreateFromImage(image);
            ReaderModes(texture);
            Vector2 u=new(4/16f,2/16f),v=new(12/16f,2/16f),w=new(4/16f,14/16f);
            var quad=new NativeShadowQuad(texture,new(100,200),new(108,200),new(100,212),u,v,w);
            var helper=new NativeStaticShadow();
            Check(!helper.TryContact(quad,out _),"unregistered region fails conservatively without readback");
            NativeStaticShadow.RegisterImageRegion(texture,image,new Rect2I(4,2,8,12),"synthetic-cpu-image");
            Check(helper.TryContact(quad,out var contact),"opaque native region");
            Check(contact.DistanceTo(new Vector2(104.5f,210))<.001f,"actual asymmetric opaque foot, transparent rows excluded");
            Check(NativeStaticShadow.FindContact(image,Vector2.Zero,new Vector2(4/16f,0),new Vector2(0,1))==null,
                "neighboring empty atlas region cannot borrow foot");
            var flipped=quad with { A=quad.B,B=quad.A,C=quad.B+quad.C-quad.A,U=quad.V,V=quad.U,W=quad.V+quad.W-quad.U };
            Check(helper.TryContact(flipped,out var flipContact) && flipContact.DistanceTo(contact)<.001f,
                "equivalent flipped geometry and UV retain contact");
            foreach(float zoom in new[]{.5f,1f,2f}) {
                var view=new Transform2D(new Vector2(zoom,0),new Vector2(0,zoom),new Vector2(-37.25f,81.5f));
                var stamp=quad.Project(contact,new Vector2(2,1),10,.5f,view);
                Check(stamp.U==quad.U && stamp.V==quad.V && stamp.W==quad.W,"exact native UV preserved");
                Check(NativeStaticShadow.TryMap(quad,new Vector2(8.5f/16,12/16f),out var mapped)
                    && (view*mapped).DistanceTo(view*contact)<.001f,"pan/zoom contact invariant");
            }
            var sheared=quad with { B=quad.B+new Vector2(2,3),C=quad.C+new Vector2(-4,0) };
            Check(helper.TryContact(sheared,out var shearContact)
                && shearContact.DistanceTo(sheared.A+(sheared.B-sheared.A)*(.5625f)+(sheared.C-sheared.A)*(10/12f))<.001f,
                "actual affine deformation used");
            var resizedFrame=quad with { V=new Vector2(10/16f,2/16f),W=new Vector2(4/16f,12/16f),B=new(106,200),C=new(100,210) };
            NativeStaticShadow.RegisterImageRegion(texture,image,new Rect2I(4,2,6,10),"synthetic-second-frame");
            Check(helper.TryContact(resizedFrame,out var frameContact) && frameContact.DistanceTo(contact)<.001f,
                "changed frame region is independent of graphic cache");
            var degenerate=quad with { W=quad.V };
            Check(!NativeStaticShadow.TryMap(degenerate,u,out _),"degenerate UV rejected");
            Check(helper.TryContactFromPage(quad,image,1,out var unchanged)&&unchanged.DistanceTo(contact)<.001f,
                "borrowed CPU page initial source version");
            for(int x=7;x<10;x++)image.SetPixel(x,11,Colors.Transparent);
            Check(helper.TryContactFromPage(quad,image,1,out var sameVersion)&&sameVersion.DistanceTo(contact)<.001f,
                "same source version uses cached scalar contact");
            Check(helper.TryContactFromPage(quad,image,2,out var repainted)&&Math.Abs(repainted.Y-209)<.001f,
                "in-place partial atlas repaint refreshes the entire affected sprite contact");
            for(int y=2;y<14;y++)for(int x=4;x<12;x++)image.SetPixel(x,y,new Color(1,0,0,0));
            Check(!helper.TryContactFromPage(quad,image,3,out _),"changed-version zero alpha rejects nonzero RGB");
            GD.Print("[native-static-shadow-probe] PASS synthetic=1 alpha-contact=pass atlas=pass flip=pass affine=pass frame-region=pass zoom=0.5,1,2 native-scene=not-tested");
            GetTree().Quit(0);
        }
        catch(Exception error) { GD.PrintErr("[native-static-shadow-probe] FAIL "+error);GetTree().Quit(1); }
    }

    // Exercise the actual reader over one synthetic recorded quad. This tests
    // classification/transport only; it deliberately claims no native admission.
    private static void ReaderModes(Texture2D texture)
    {
        const BindingFlags hidden=BindingFlags.Instance|BindingFlags.NonPublic;
        using var capture=new OrderedWorldCapture(null,null);
        T Field<T>(string name)=>(T)typeof(OrderedWorldCapture).GetField(name,hidden).GetValue(capture);
        var dynamic=Field<List<OrderedWorldResidency.Quad>>("_dynamic");
        var occurrences=Field<List<OrderedWorldResidency.Occurrence>>("_occurrences");
        var states=Field<List<OrderedWorldDevice.State>>("_states");
        var commands=Field<List<OrderedWorldCanvas.Command>>("_commands");
        Field<Dictionary<Rid,Texture2D>>("_shadowTextureOwners").Add(texture.GetRid(),texture);
        occurrences.Add(new OrderedWorldResidency.Occurrence(0));
        states.Add(new OrderedWorldDevice.State{Xx=1,Yy=1});
        commands.Add(new OrderedWorldCanvas.Command{FirstCorner=0,Texture=texture.GetRid(),Group=1});
        OrderedWorldDevice.Vertex Vertex(float x,float y,float u,float v,int mode,bool transparency) {
            var hue=UltimaBatcher2D.Encode(new Vector3(0,mode,transparency ? 1.5f : 1));
            return new OrderedWorldDevice.Vertex{PositionUv=new(x,y,u,v),PackedHue=new(hue.R,hue.G,hue.B,hue.A)};
        }
        foreach(bool transparency in new[]{false,true}) {
            foreach(int mode in new[]{0,1,2,20,21,22}) {
                var q=new OrderedWorldResidency.Quad{
                    A=Vertex(1,2,0,0,mode,transparency),B=Vertex(9,2,1,0,mode,transparency),
                    C=Vertex(1,14,0,1,mode,transparency),D=Vertex(9,14,1,1,mode,transparency)};
                dynamic.Clear();dynamic.Add(q);
                Check(capture.ReadSpeedShadowQuad(0,Transform2D.Identity,out var actual)
                    &&actual.A==new Vector2(1,2)&&actual.B==new Vector2(9,2)&&actual.W==new Vector2(0,1),
                    $"native body mode{mode} transparency={transparency} retained with exact corners/UV");
                Check(dynamic[0].Equals(q),"reader does not mutate original native/fallback color quad");
                foreach(int shadow in new[]{8,28})for(int corner=0;corner<4;corner++) {
                    var mixed=q;var rejected=Vertex(1,2,0,0,shadow,transparency);
                    switch(corner){case 0:mixed.A.PackedHue=rejected.PackedHue;break;
                        case 1:mixed.B.PackedHue=rejected.PackedHue;break;
                        case 2:mixed.C.PackedHue=rejected.PackedHue;break;
                        case 3:mixed.D.PackedHue=rejected.PackedHue;break;}
                    dynamic[0]=mixed;
                    Check(!capture.ReadSpeedShadowQuad(0,Transform2D.Identity,out _),
                        $"legacy mode{shadow} corner{corner} transparency={transparency} excluded");
                    Check(dynamic[0].Equals(mixed),"rejected legacy quad preserved for fallback");
                }
            }
        }
    }
}
