// SPDX-License-Identifier: BSD-2-Clause
// Task1054: cold, disconnected fixture observation; no production call sites.
#if DEBUG
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using GUO.Game.GameObjects;
using GUO.Game.Map;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using GUO.Configuration;
using GUO.Assets;
using GUO.Renderer;
using GUO.Renderer.DepthWorld;
using GUO.Renderer.SpeedMode;
using GUO.Renderer.SpeedMode.Shadows;
using N3=System.Numerics.Vector3;

namespace GUO.Game.Scenes;

internal partial class GameScene
{
    // Call on the stopped fixture's game thread after its completed draw.
    // Copy JSON before another draw; render callbacks never read live scene objects.
    internal string CaptureSpeedLightDiagnostics(UltimaBatcher2D batcher)
    {
        if(!_attributionGuard.CheckThread(Environment.CurrentManagedThreadId))
            return JsonSerializer.Serialize(new{provenanceValid=false,reason="managed-thread-mismatch"});
        if(GUO.Network.NetClient.Socket.IsConnected)
            throw new InvalidOperationException("Light attribution is disconnected-fixture only.");
        var capture=batcher.OrderedWorldProbe;
        if(capture==null) throw new InvalidOperationException("Ordered capture is required.");
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        T Read<T>(string name)=>(T)capture.GetType().GetField(name,flags).GetValue(capture);
        var device=Read<OrderedWorldDevice>("_device");
        var occurrences=Read<List<OrderedWorldResidency.Occurrence>>("_occurrences");
        var dynamic=Read<List<OrderedWorldResidency.Quad>>("_dynamic");
        int classic=0;
        foreach(var occurrence in occurrences) {
            ref readonly var q=ref occurrence.Get(CollectionsMarshal.AsSpan(dynamic));
            int mode=((int)MathF.Floor(q.A.PackedHue.Z*255+.5f))&0x3f;
            if(mode==8) classic++;
        }
        var profile=ProfileManager.CurrentProfile;
        var binding=SpeedModeBindings.Current;
        var frame=_speedLights;
        var inputs=_speedShadows;
        var offers=new List<object>();
        var foot=new N3(_world.Player.X+(_world.Player.Offset.X+_world.Player.Offset.Y)/44f,
            _world.Player.Y+(_world.Player.Offset.Y-_world.Player.Offset.X)/44f,_world.Player.Z);
        Span<MobileLampShadow> affecting=stackalloc MobileLampShadow[2];
        int count=inputs==null?0:MobileLampProjection.Select(inputs.Lamps.AsSpan(0,inputs.LampCount),foot,affecting);
        if(frame!=null) for(int i=0;i<frame.Count;i++) {
            var e=frame.Lights[i];var s=e.Source;int row=e.ShadowRow;
            object projection=null;
            for(int j=0;j<count;j++) if(affecting[j].Offer==i) {
                var a=affecting[j];
                projection=new { directionX=a.Direction.X,directionY=a.Direction.Y,a.Length,a.Strength };
            }
            offers.Add(new { offer=i,s.X,s.Y,s.Z,s.OffsetX,s.OffsetY,s.Radius,s.Energy,
                colour=new[]{s.Color.R,s.Color.G,s.Color.B},curve=s.Curve.ToString(),e.Flicker,
                pixelCentre=new[]{e.PixelCentre.X,e.PixelCentre.Y},e.PixelRadius,
                shadowPosition=inputs!=null&&i<inputs.LampCount
                    ?new[]{inputs.Lamps[i].Position.X,inputs.Lamps[i].Position.Y,inputs.Lamps[i].Position.Z}:null,
                row,mobileMask=e.MobileShadow.IsValid,
                stamps=inputs!=null&&row>=0&&row<inputs.Mobile.Length?inputs.Mobile[row]?.Count??0:0,projection });
        }
        var receivers=new List<object>();
        if(_rtField!=null) for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++) {
            int x=_world.Player.X+dx,y=_world.Player.Y+dy;
            receivers.Add(new{x,y,groundZ=_rtField.GroundZ(x,y),coverZ=_rtField.CoverZ(x,y),
                roof=_rtField.CoverIsRoofFlag(x,y)});
        }
        return JsonSerializer.Serialize(new {
            tick=Time.Ticks,player=new{_world.Player.X,_world.Player.Y,_world.Player.Z},
            profile=new{profile.ShadowsEnabled,profile.ShadowsStatics,profile.LightingSpeedMode,
                profile.SpeedModeSunShadows,profile.SpeedModeLampShadows},
            capturedClassicMode8Occurrences=classic, // Capture count, never a pixel/output claim.
            OrderedWorldCapture.LastFrameOnPass,OrderedWorldCapture.LastFallback,
            OrderedWorldCapture.FramesDrawn,OrderedWorldCapture.FramesFallback,
            device.SpeedLightReady,binding.Sequence,surfacePublished=binding.Surface!=null,
            lightPublished=binding.LightTarget!=null,
            origin=new[]{binding.Origin.X,binding.Origin.Y},
            targetBounds=new[]{binding.TargetPixelBounds.X,binding.TargetPixelBounds.Y,binding.TargetPixelBounds.Z,binding.TargetPixelBounds.W},
            roofShare=frame?.RoofShare,ambient=frame==null?null:new[]{frame.Ambient.X,frame.Ambient.Y,frame.Ambient.Z},receivers,
            frameOrigin=frame==null?null:new[]{frame.Origin.X,frame.Origin.Y},
            frameTarget=frame==null?null:new[]{frame.TargetSize.X,frame.TargetSize.Y},
            sunMask=frame?.SunMask.IsValid??false,sunStamps=inputs?.Sun.Count??0,
            lampAtlas=frame?.LampShadowAtlas.IsValid??false,SpeedSunSource,SpeedMobileQuadCount,offers
        });
    }

    // Pure lifetime gate: caller-supplied IDs permit regression without Godot/world access.
    private sealed class AttributionGuard
    {
        internal int OwnerThread { get; private set; }
        private int _armed,_invalid;
        internal bool Armed=>Volatile.Read(ref _armed)!=0;
        internal bool Invalid=>Volatile.Read(ref _invalid)!=0;
        internal void Begin(int thread) { OwnerThread=thread;Volatile.Write(ref _invalid,0);Volatile.Write(ref _armed,1); }
        internal bool CheckThread(int thread) {
            if(thread!=OwnerThread){Interlocked.Exchange(ref _invalid,1);return false;}
            return !Invalid;
        }
        internal bool Observe(int thread)=>Armed&&CheckThread(thread);
        internal void Cancel()=>Volatile.Write(ref _armed,0);
        internal static bool? Present(int matches,bool complete)=>matches>0?true:complete?false:null;
        internal static bool Complete(int seen,int total)=>seen==total;
        internal static bool Take(int seen,int limit)=>seen<limit;
    }
    private const int AttributionNeighbourCap=256,AttributionCachedCap=256,AttributionChunkCap=512;
    private readonly AttributionGuard _attributionGuard=new();
    private sealed class AttributionRegressionNode { internal AttributionRegressionNode Next; }

    // Pure regression preparation: no Godot, world, filesystem or actual threads.
    // Executed by a future admitted fixture before boot; not run in the source-only phase.
    internal static string[] LightAdmissionSourceRegression()
    {
        var passed=new List<string>();
        void Check(bool value,string name){if(!value)throw new InvalidOperationException(name);passed.Add(name);}
        var gate=new AttributionGuard();gate.Begin(7);
        Check(gate.Observe(7),"owner-thread observation admitted");
        int liveTouches=0;if(gate.Observe(8))liveTouches++;
        Check(liveTouches==0&&gate.Invalid,"wrong-thread observer skips live access and invalidates provenance");
        Check(!gate.Observe(7),"owner cannot finish with valid provenance after thread mismatch");
        gate.Cancel();Check(!gate.Armed&&!gate.Observe(7),"cancel retires arming");
        gate.Begin(9);Check(gate.Observe(9)&&!gate.Invalid,"new scope resets invalid provenance");
        gate.Cancel();
        void Failure(string name) {
            var marker=new InvalidOperationException(name);Exception caught=null;
            try { gate.Begin(11);try{throw marker;}finally{gate.Cancel();} }
            catch(Exception error){caught=error;}
            Check(ReferenceEquals(caught,marker)&&!gate.Armed,name+" propagates same exception and retires scope");
        }
        Failure("draw failure");Failure("readback failure");Failure("finish serialization failure");
        foreach(int limit in new[]{AttributionNeighbourCap,AttributionCachedCap,AttributionChunkCap,256}) {
            int seen=0;while(AttributionGuard.Take(seen,limit))seen++;
            Check(seen==limit&&!AttributionGuard.Take(seen,limit),"bounded census/event limit "+limit);
            Check(AttributionGuard.Complete(seen,limit),"exact-limit complete receipt "+limit);
            Check(!AttributionGuard.Complete(seen,limit+1),"cap-plus-one incomplete receipt "+limit);
        }
        var cycle=new AttributionRegressionNode();cycle.Next=cycle;
        var cursor=cycle;int nodes=0;
        while(cursor!=null&&AttributionGuard.Take(nodes,AttributionNeighbourCap)){nodes++;cursor=cursor.Next;}
        Check(cursor!=null&&nodes==AttributionNeighbourCap,"cyclic neighbour terminates with incomplete census");
        int remaining=AttributionCachedCap,firstCache=Math.Min(200,remaining);remaining-=firstCache;
        int secondCache=Math.Min(200,remaining);
        Check(firstCache+secondCache==AttributionCachedCap&&!AttributionGuard.Complete(secondCache,200),"cached-light budget is global across matching chunks");
        Check(AttributionGuard.Present(0,false)==null,"truncated zero membership is unknown");
        Check(AttributionGuard.Present(0,true)==false,"complete zero membership may establish absence");
        Check(AttributionGuard.Present(1,false)==true,"positive witness survives truncation without establishing completeness");
        return passed.ToArray();
    }

    private Static _attributionLamp;
    private string _attributionLabel;
    private object _attributionBefore;
    private readonly List<object> _attributionEvents=new();
    private long _attributionWalks,_attributionReused,_attributionTiles;
    private bool _attributionTruncated;

    // Fixture calls only with processing stopped, before its explicit DrawFrame.
    internal void BeginLightAdmissionProbe(Static lamp,string label)
    {
        CancelLightAdmissionProbe();
        _attributionGuard.Begin(Environment.CurrentManagedThreadId);
        try {
            if(lamp==null||GUO.Network.NetClient.Socket.IsConnected)
                throw new InvalidOperationException("Exact disconnected fixture lamp required.");
            _attributionLamp=lamp;_attributionLabel=label;_attributionEvents.Clear();_attributionTruncated=false;
            _attributionWalks=ChunkWalkRecords;_attributionReused=ChunksReused;_attributionTiles=TilesReused;
            _attributionBefore=AttributionAdmissionState();
        }
        catch { CancelLightAdmissionProbe();throw; }
    }

    // No live-object access; safe even if an awaited fixture continuation changes thread.
    internal void CancelLightAdmissionProbe()
    {
        _attributionGuard.Cancel();_attributionLamp=null;_attributionBefore=null;
    }

    internal string FinishLightAdmissionProbe()
    {
        try {
            int thread=Environment.CurrentManagedThreadId;
            if(!_attributionGuard.Observe(thread))return JsonSerializer.Serialize(new{
                provenanceValid=false,reason="unarmed-or-invalid-thread",ownerThread=_attributionGuard.OwnerThread,
                currentThread=thread,eventsTruncated=_attributionTruncated});
            object after=AttributionAdmissionState();
            return JsonSerializer.Serialize(new {
                provenanceValid=!_attributionGuard.Invalid,ownerThread=_attributionGuard.OwnerThread,label=_attributionLabel,
                before=_attributionBefore,after,
                frameAggregateDeltas=new{walks=ChunkWalkRecords-_attributionWalks,
                    chunksReused=ChunksReused-_attributionReused,tilesReused=TilesReused-_attributionTiles},
                events=_attributionEvents,eventsTruncated=_attributionTruncated,eventsComplete=!_attributionTruncated,
                actualBoundaryHooksObserved=_attributionEvents.Count>0,
                limitation="Stopped game-thread bounded before/after state. Incomplete census or truncated events never proves absence. Actual path requires admitted hooks. Postdraw source count may already be reset."});
        }
        finally { CancelLightAdmissionProbe(); }
    }

    private static object AttributionObject(GameObject obj)
    {
        if(obj==null)return null;
        ref var tiles=ref Client.Game.UO.FileManager.TileData.StaticData;
        bool typed=obj is Static or Multi,valid=typed&&obj.Graphic<tiles.Length;
        return new{identity=RuntimeHelpers.GetHashCode(obj),type=obj.GetType().Name,
            index=obj is Static s?(int?)s.Index:null,obj.Graphic,obj.X,obj.Y,obj.Z,
            obj.AllowedToDraw,obj.AlphaHue,obj.IsDestroyed,obj.InChunkMesh,obj.MeshSpriteIndex,
            flags=valid?(ulong?)tiles[obj.Graphic].Flags:null,
            transparent=valid?(bool?)tiles[obj.Graphic].IsTransparent:null,
            light=valid?(bool?)tiles[obj.Graphic].IsLight:null,
            layer=valid?(int?)tiles[obj.Graphic].Layer:null,
            height=valid?(int?)tiles[obj.Graphic].Height:null};
    }

    private object AttributionAdmissionState()
    {
        if(!_attributionGuard.Observe(Environment.CurrentManagedThreadId))
            return new{provenanceValid=false,reason="invalid-thread-before-live-access"};
        var lamp=_attributionLamp;
        int nx=lamp.X+1,ny=lamp.Y+1;
        sbyte threshold=unchecked((sbyte)(lamp.Z+5)); // Preserve AddLight's actual cast.
        var neighbour=new List<object>();
        var first=_world.Map.GetTile(nx,ny,false); // Existing exact neighbour only; never load a map tile.
        var obj=first;bool blockerFound=false;
        while(obj!=null&&AttributionGuard.Take(neighbour.Count,AttributionNeighbourCap)) {
            bool opaque=obj is Static st&&!st.ItemData.IsTransparent
                ||obj is Multi multi&&!multi.ItemData.IsTransparent;
            bool blocks=opaque&&obj.AllowedToDraw&&obj.Z>=threshold&&obj.Z<_maxZ;
            blockerFound|=blocks;
            neighbour.Add(new{obj=AttributionObject(obj),opaque,passesAllowed=obj.AllowedToDraw,
                passesLowerZ=obj.Z>=threshold,passesUpperZ=obj.Z<_maxZ,wouldBlock=blocks});
            obj=obj.TNext;
        }
        bool neighbourComplete=first!=null&&obj==null; // Null nonloading head is unknown, never proof of absence.
        var chunks=new List<object>();
        int visibleTotal=_visibleChunks.Count,visibleSeen=Math.Min(visibleTotal,AttributionChunkCap),cachedRemaining=AttributionCachedCap;
        bool allCachesComplete=true;
        for(int ci=0;ci<visibleSeen;ci++) {
            var chunk=_visibleChunks[ci];
            if(chunk==null||chunk.IsDestroyed||chunk.X!=(lamp.X>>3)||chunk.Y!=(lamp.Y>>3))continue;
            var walk=chunk.Walk;int tile=((lamp.X&7)<<3)|(lamp.Y&7);ulong bit=1UL<<tile;
            var cached=new List<object>();int exactMatches=0;
            int cachedTotal=walk?.Lights.Count??0,cachedSeen=Math.Min(cachedTotal,cachedRemaining);
            cachedRemaining-=cachedSeen;
            bool cachedComplete=AttributionGuard.Complete(cachedSeen,cachedTotal);allCachesComplete&=cachedComplete;
            for(int li=0;li<cachedSeen;li++) {
                var entry=walk.Lights[li];
                bool exact=ReferenceEquals(entry.Light,lamp);if(exact)exactMatches++;
                cached.Add(new{entry.Tile,exactFixtureIdentity=exact,obj=AttributionObject(entry.Light)});
            }
            chunks.Add(new{chunk.X,chunk.Y,chunk.ObjectsVersion,chunk.ScreenPositionsStale,
                dirtyMask=chunk.DirtyTiles.ToString("X16"),tile,bit=bit.ToString("X16"),
                priorRecordExists=walk!=null,priorReusable=walk?.Reusable,
                dynamicMask=walk?.DynamicMask.ToString("X16"),walkedDynamic=walk?.WalkedDynamic.ToString("X16"),
                staleMask=walk?.StaleTiles.ToString("X16"),offScreenCleared=walk?.OffScreenCleared,
                cachedKey=walk?.Key is ReuseKey key?new{key.MaxZ,key.MaxGroundZ,key.NoDrawRoofs,key.HideVegetation}:null,
                candidateDynamicOrDirty=walk==null?null:(walk.DynamicMask|chunk.DirtyTiles).ToString("X16"),
                cachedLampMatches=exactMatches,cachedLampPresent=AttributionGuard.Present(exactMatches,cachedComplete),
                cachedCensus=new{seen=cachedSeen,total=cachedTotal,complete=cachedComplete,truncated=!cachedComplete},cached,
                mesh=new{landStamp=chunk.Mesh.Land.ContentStamp,staticStamp=chunk.Mesh.Statics.ContentStamp,
                    landCount=chunk.Mesh.Land.Count,staticCount=chunk.Mesh.Statics.Count},
                savedMesh=walk==null?null:new{walk.LandStamp,walk.StaticStamp,walk.LandCount,walk.StaticCount},
                warning="Candidate mask is not ReusableTiles result; view/key/mouse/build guards still apply. Never call ReusableTiles diagnostically: it re-adds offers and mutates state."});
        }
        bool visibleComplete=AttributionGuard.Complete(visibleSeen,visibleTotal);
        return new{provenanceValid=true,lamp=AttributionObject(lamp),
            censusComplete=neighbourComplete&&visibleComplete&&allCachesComplete,
            visibleCensus=new{seen=visibleSeen,total=visibleTotal,complete=visibleComplete,truncated=!visibleComplete},
            targetChunkPresent=AttributionGuard.Present(chunks.Count,visibleComplete),
            exactNeighbour=new{x=nx,y=ny,loadedHeadPresent=first!=null,
                threshold=(int)threshold,chain=neighbour,complete=neighbourComplete,truncated=!neighbourComplete,
                blockerPresent=AttributionGuard.Present(blockerFound?1:0,neighbourComplete)},
            candidateStaticLightId=lamp.Graphic==0x0B1D||lamp.Graphic>=0x3E02&&lamp.Graphic<=0x3E0B
                ||lamp.Graphic>=0x3914&&lamp.Graphic<=0x3929?2:(int)lamp.ItemData.Layer,
            lightIdLimitation="Raw static candidate only; exact post-colour-remap validation requires lightId boundary event.",
            limits=new{maxZ=_maxZ,maxGroundZ=_maxGroundZ,noDrawRoofs=_noDrawRoofs},
            counts=new{classicLights=_lightCount,sourceCount=_rtSourceCount,
                maximum=LightsLoader.MAX_LIGHTS_DATA_INDEX_COUNT,
                classicCapacityReject=_lightCount>=LightsLoader.MAX_LIGHTS_DATA_INDEX_COUNT,
                sourceCapacityReject=_rtSourceCount>=LightsLoader.MAX_LIGHTS_DATA_INDEX_COUNT,
                addLightModeReject=!UseLights&&!UseAltLights&&!UseRealtimeLights&&!UseSpeedLights,
                recordSourceModeReject=!UseRealtimeLights&&!UseSpeedLights,
                sourceArrayCapacity=_rtSources?.Length??0,publishedOfferCount=_speedLights?.Count??0,
                countTiming="Snapshot only; source count resets after DrawSpeedLights; actual admission counts require boundary hook."},
            mode=new{UseLights,UseAltLights,UseRealtimeLights,UseSpeedLights,
                SpeedModeDebug.Requested,SpeedModeDebug.Supported,SpeedModeDebug.RuntimeUnavailableReason,
                dead=_world.Player.IsDead,hidden=_world.Player.IsHidden,ProfileManager.CurrentProfile.EnableBlackWhiteEffect,
                ChunkReuse,resident=DepthWorldProbe.ResidentEnabled},chunks,
            geometry=new{offsetX=_offset.X,offsetY=_offset.Y,minX=_minPixel.X,minY=_minPixel.Y,maxX=_maxPixel.X,maxY=_maxPixel.Y}};
    }

    // Proposed shared call sites are NOT applied. These are armed only by the fixture.
    internal void ObserveAttributionLight(GameObject obj,string stage,GameObject blocker=null,int? lightId=null)
    {
        if(!_attributionGuard.Observe(Environment.CurrentManagedThreadId))return;
        if(_attributionLamp==null||!ReferenceEquals(obj,_attributionLamp))return;
        if(_attributionEvents.Count>=256){_attributionTruncated=true;return;}
        _attributionEvents.Add(new{stage,lightId,classicCount=_lightCount,sourceCount=_rtSourceCount,
            maxZ=_maxZ,threshold=unchecked((sbyte)(_attributionLamp.Z+5)),
            lamp=AttributionObject(obj),blocker=AttributionObject(blocker)});
    }

    internal void ObserveAttributionChunk(Chunk chunk,ulong? actualWalkMask,bool built,string outcome=null)
    {
        if(!_attributionGuard.Observe(Environment.CurrentManagedThreadId))return;
        if(_attributionLamp==null||chunk.X!=(_attributionLamp.X>>3)||chunk.Y!=(_attributionLamp.Y>>3))return;
        if(_attributionEvents.Count>=256){_attributionTruncated=true;return;}
        _attributionEvents.Add(new{stage="actual-chunk-decision",built,
            actualWalkMask=actualWalkMask?.ToString("X16"),
            reuseDecision=outcome??(actualWalkMask==null?"full-walk-required":actualWalkMask==0?"fully-reused":"partial-reuse"),
            chunk.X,chunk.Y,priorRecordExists=chunk.Walk!=null,
            dirtyMask=chunk.DirtyTiles.ToString("X16"),dynamicMask=chunk.Walk?.DynamicMask.ToString("X16")});
    }

}
#endif
