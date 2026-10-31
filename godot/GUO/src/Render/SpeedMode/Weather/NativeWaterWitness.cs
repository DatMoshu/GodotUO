// SPDX-License-Identifier: BSD-2-Clause
// Task1043: one in-memory native-art witness; no authored receiver data.
using System;
using Godot;
using GUO.Game;
using GUO.Game.GameObjects;

namespace GUO.Renderer.SpeedMode.Weather;

internal sealed class NativeWaterWitness : IDisposable
{
    // This existing water coordinate is taken from preserved1040 native readback,
    // not discovered by another map hunt. Ordinary entity placement only.
    internal const ushort DonorX=1441,DonorY=1739,WaterX=1441,WaterY=1740;
    internal const sbyte WaterZ=-5;
    internal static readonly ushort[] Choices={0x0080};
    private readonly World _world;
    private Item _donor;
    private int _attempts;
    internal Item Donor=>_donor;
    internal bool HasDonor=>_donor!=null&&!_donor.IsDestroyed;
    internal object ReceiverMetadata { get; private set; }
    internal NativeWaterWitness(World world)=>_world=world;
    internal bool ExistingWater()
    {
        for(var obj=_world.Map.GetTile(WaterX,WaterY,true);obj!=null;obj=obj.TNext) {
            if(obj is Land land&&land.TileData.IsWet&&obj.Z==WaterZ) {
                ReceiverMetadata=new{kind="existing-land",graphic=obj.Graphic,name=land.TileData.Name,x=obj.X,y=obj.Y,z=obj.Z};return true;
            }
            if(obj is Static native&&native.ItemData.IsWet&&obj.Z==WaterZ) {
                ReceiverMetadata=new{kind="existing-static",graphic=obj.Graphic,name=native.ItemData.Name,x=obj.X,y=obj.Y,z=obj.Z};return true;
            }
        }
        return false;
    }
    internal object Install(ushort graphic)
    {
        if(_attempts>=Choices.Length) throw new InvalidOperationException("Only the SAME0080 donor is authorized");
        Dispose();_attempts++;
        ref var tile=ref Client.Game.UO.FileManager.TileData.StaticData[graphic];
        // Keep metadata authoritative; never set flags, heights or surface rows.
        if(tile.IsWet||tile.IsRoof||(!tile.IsWall&&!tile.IsWindow&&!tile.IsDoor&&tile.IsSurface&&tile.Height<4))
            return new{graphic,name=tile.Name,height=tile.Height,admissible=false,attempt=_attempts};
        _donor=_world.GetOrCreateItem((uint)(0x40001000+_attempts));
        _donor.Graphic=graphic;_donor.Amount=1;_donor.SetInWorldTile(DonorX,DonorY,WaterZ);
        _donor.CheckGraphicChange(); // Normal native entity admission, as after a graphic packet.
        if(!_donor.AllowedToDraw) throw new InvalidOperationException("Native donor art not drawable");
        return new {graphic,name=tile.Name,height=tile.Height,admissible=true,wall=tile.IsWall,window=tile.IsWindow,
            door=tile.IsDoor,drawable=_donor.AllowedToDraw,x=DonorX,y=DonorY,z=WaterZ,attempt=_attempts};
    }
    internal readonly record struct Donors(int WaterPixels,int Eligible,int Controlled,bool[] ControlledPixels);
    internal Donors ReadActualGeometry(Image surface,Vector2I origin,float artScale,double seconds)
    {
        int width=surface.GetWidth(),height=surface.GetHeight(),water=0,eligible=0,controlled=0;
        var mask=new bool[width*height];
        for(int y=0;y<height;y++) for(int x=0;x<width;x++) {
            var receiver=surface.GetPixel(x,y);int data=(int)MathF.Round(receiver.A);
            if((data&15)!=6||(data&16)!=0) continue;
            water++;
            float wave=MathF.Floor(MathF.Sin((receiver.R+origin.X)*1.7f+(receiver.G+origin.Y)*2.3f+(float)seconds*1.4f)*1.5f+.5f);
            for(int k=1;k<=4;k++) {
                int sx=(int)MathF.Floor(x+.5f+wave*artScale),sy=(int)MathF.Floor(y+.5f-k*16*artScale);
                if(sx<0||sy<0||sx>=width||sy>=height) continue;
                var source=surface.GetPixel(sx,sy);int kind=(int)MathF.Round(source.A)&15;
                if(kind!=2&&kind!=3&&kind!=4) continue;
                float dz=source.B-receiver.B;
                // Diagnostic follows the shader: 4 native pixels per UO Z.
                if(dz<=0||MathF.Abs(dz-k*4f)>.125f) continue;
                if(MathF.Abs((source.R-receiver.R-source.G+receiver.G)*22-wave)>1) continue;
                eligible++;
                float wx=source.R+origin.X,wy=source.G+origin.Y;
                // Restrict qualification to the installed donor's actual native
                // surface footprint and neighbouring existing water, not distant art.
                bool donor=_donor!=null&&!_donor.IsDestroyed
                    &&wx>=DonorX-.05f&&wx<=DonorX+1.05f&&wy>=DonorY-.05f&&wy<=DonorY+1.05f
                    &&MathF.Abs(receiver.R+origin.X-WaterX)<3&&MathF.Abs(receiver.G+origin.Y-WaterY)<3;
                if(donor) { mask[y*width+x]=true;controlled++; }
                break;
            }
        }
        return new Donors(water,eligible,controlled,mask);
    }
    internal static int ChangedControlled(Image before,Image after,bool[] controlled)
    {
        var a=before.GetData();var b=after.GetData();int changed=0;
        if(a.Length!=b.Length||a.Length!=controlled.Length*4) throw new InvalidOperationException("Matching native geometry/capture sizes required");
        for(int p=0;p<controlled.Length;p++) {
            if(!controlled[p]) continue;int i=p*4;
            if(a[i]!=b[i]||a[i+1]!=b[i+1]||a[i+2]!=b[i+2]||a[i+3]!=b[i+3]) changed++;
        }
        return changed;
    }
    public void Dispose()
    {
        if(_donor!=null&&!_donor.IsDestroyed) { _donor.RemoveFromTile();_donor.Destroy(); }
        _donor=null;
    }
}
