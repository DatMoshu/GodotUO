// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Godot;

namespace GUO.Renderer.SpeedMode.Shadows;

// Producer metadata is keyed by actual texture identity and atlas region.
// Weak keys never retain textures. No texture readback, including cold frames.
internal sealed class NativeStaticShadow
{
    private const int RegionsPerPage=512;
    private readonly int _snapshotThread=System.Environment.CurrentManagedThreadId;
    private sealed class Page
    {
        internal readonly int Width,Height;
        internal readonly Dictionary<Rect2I,(Vector2? Contact,string Source,int Version)> Regions=new();
        internal readonly Queue<Rect2I> Order=new();
        internal Page(Texture2D texture) { Width=texture.GetWidth();Height=texture.GetHeight(); }
    }
    private static readonly ConditionalWeakTable<Texture2D,Page> Pages=new();

    private static void StoreUv(Page page,Rect2I region,Vector2? uv,string source,int version)
    {
        lock(page) {
            if(!page.Regions.ContainsKey(region)) {
                // Evict one oldest region, never reset a whole warm page.
                if(page.Regions.Count>=RegionsPerPage)page.Regions.Remove(page.Order.Dequeue());
                page.Order.Enqueue(region);
            }
            page.Regions[region]=(uv,source,version);
        }
    }

    private static void Store(Texture2D texture,Rect2I region,Vector2? pixel,string source)
    {
        var page=Pages.GetValue(texture,t=>new Page(t));
        Vector2? uv=pixel.HasValue ? (pixel.Value+(Vector2)region.Position)/new Vector2(page.Width,page.Height) : null;
        StoreUv(page,region,uv,source,-1);
    }

    // Theme loaders/preview already hold this CPU Image before texture upload.
    internal static void RegisterTheme(Texture2D texture,Image image)
        =>RegisterImageRegion(texture,image,new Rect2I(0,0,image.GetWidth(),image.GetHeight()),"theme-cpu-image");

    internal static void RegisterImageRegion(Texture2D texture,Image image,Rect2I region,string source)
    {
        var size=new Vector2(image.GetWidth(),image.GetHeight());
        Vector2 u=(Vector2)region.Position/size,v=(Vector2)(region.Position+new Vector2I(region.Size.X,0))/size;
        Vector2 w=(Vector2)(region.Position+new Vector2I(0,region.Size.Y))/size;
        Vector2? uv=FindContact(image,u,v,w);
        Store(texture,region,uv.HasValue ? uv.Value*size-(Vector2)region.Position : null,source);
    }

    // GameScene constructs this helper on its owning game thread. Borrow CPU
    // atlas pages only at that same native snapshot seam, before QueueSpeedLight.
    // No borrowed Image is retained or disposed; callbacks receive scalar stamps.
    internal bool TryContact(in NativeShadowQuad quad,out Vector2 contact)
    {
        contact=default;
        if(System.Environment.CurrentManagedThreadId!=_snapshotThread || quad.Texture==null)return false;
        if(TextureAtlas.TryGetPage(quad.Texture,out Image borrowed,out int version))
            return TryContactFromPage(quad,borrowed,version,out contact);
        // Nonatlas/theme regions need explicit CPU-loader registration.
        return TryCachedContact(quad,null,-1,out contact);
    }

    internal bool TryContactFromPage(in NativeShadowQuad quad,Image borrowed,int version,out Vector2 contact)
    {
        contact=default;
        if(System.Environment.CurrentManagedThreadId!=_snapshotThread || borrowed==null || quad.Texture==null)return false;
        return TryCachedContact(quad,borrowed,version,out contact);
    }

    private static bool TryCachedContact(in NativeShadowQuad quad,Image borrowed,int version,out Vector2 contact)
    {
        contact=default;
        if(!Pages.TryGetValue(quad.Texture,out var page)) {
            if(borrowed==null)return false;
            page=Pages.GetValue(quad.Texture,t=>new Page(t));
        }
        Vector2 d=quad.V+quad.W-quad.U;
        Vector2 lo=quad.U.Min(quad.V).Min(quad.W).Min(d),hi=quad.U.Max(quad.V).Max(quad.W).Max(d);
        if(!lo.IsFinite() || !hi.IsFinite() || lo.X<0 || lo.Y<0 || hi.X>1 || hi.Y>1)return false;
        var region=new Rect2I((int)MathF.Floor(lo.X*page.Width),(int)MathF.Floor(lo.Y*page.Height),0,0);
        region.Size=new Vector2I((int)MathF.Ceiling(hi.X*page.Width)-region.Position.X,
            (int)MathF.Ceiling(hi.Y*page.Height)-region.Position.Y);
        Vector2? uv=null;bool current;
        lock(page) {
            current=page.Regions.TryGetValue(region,out var metadata)&&metadata.Version==version;
            if(current)uv=metadata.Contact;
        }
        if(!current) {
            if(borrowed==null || borrowed.GetWidth()!=page.Width || borrowed.GetHeight()!=page.Height)return false;
            uv=FindContact(borrowed,quad.U,quad.V,quad.W);
            StoreUv(page,region,uv,"borrowed-atlas-cpu-page",version);
        }
        return uv.HasValue && TryMap(quad,uv.Value,out contact);
    }

    internal static Vector2? FindContact(Image image,Vector2 u,Vector2 v,Vector2 w)
    {
        // Native static UV regions are axis aligned, including flipped regions.
        // Keep UVs unchanged in the stamp; these bounds only find opaque contact.
        Vector2 d=v+w-u;
        Vector2 lo=u.Min(v).Min(w).Min(d),hi=u.Max(v).Max(w).Max(d);
        int width=image.GetWidth(),height=image.GetHeight();
        int x0=Math.Clamp((int)MathF.Floor(lo.X*width),0,width-1);
        int x1=Math.Clamp((int)MathF.Ceiling(hi.X*width)-1,0,width-1);
        int y0=Math.Clamp((int)MathF.Floor(lo.Y*height),0,height-1);
        int y1=Math.Clamp((int)MathF.Ceiling(hi.Y*height)-1,0,height-1);
        for(int y=y1;y>=y0;y--) {
            int left=-1,right=-1;
            for(int x=x0;x<=x1;x++) if(image.GetPixel(x,y).A>0) {
                if(left<0) left=x;
                right=x;
            }
            // Bottom edge of the last occupied texel; transparent padding is absent.
            if(left>=0) return new Vector2((left+right+1)*.5f/width,(y+1f)/height);
        }
        return null;
    }

    internal static bool TryMap(in NativeShadowQuad quad,Vector2 uv,out Vector2 point)
    {
        Vector2 du=quad.V-quad.U,dv=quad.W-quad.U,p=uv-quad.U;
        float determinant=du.Cross(dv);
        point=default;
        if(!float.IsFinite(determinant) || Math.Abs(determinant)<1e-12f) return false;
        point=quad.A+(quad.B-quad.A)*(p.Cross(dv)/determinant)
            +(quad.C-quad.A)*(du.Cross(p)/determinant);
        return point.IsFinite();
    }

    internal static NativeShadowQuad FromMesh(Texture2D texture,in MeshQuad quad,Vector2 offset)
        =>new(texture,quad.Position0+offset,quad.Position1+offset,quad.Position2+offset,
            quad.TextureCoordinate0,quad.TextureCoordinate1,quad.TextureCoordinate2);
}
