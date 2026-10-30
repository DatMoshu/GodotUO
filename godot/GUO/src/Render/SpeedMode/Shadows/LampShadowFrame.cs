// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned: frame-local nearest-wall-atlas mapping.
using System;
using Godot;
using N3 = System.Numerics.Vector3;

namespace GUO.Renderer.SpeedMode.Shadows;

internal sealed class LampShadowFrame : IDisposable
{
    private readonly int[] _selected=new int[10];
    private readonly ShadowLamp[] _lamps=new ShadowLamp[10];
    private readonly PolarShadowMapRd _map=new();
    private int _count;
    private bool _disposed;
    public Rid Atlas => _map.Atlas;
    public string Failure => _map.Failure;
    public bool Render(RenderingDevice device,WallShadowGrid grid,ReadOnlySpan<ShadowLamp> offers,N3 viewer,int requested)
    {
        _count=0;
        int count=PolarShadowGeometry.Select(offers,viewer,requested,_selected);
        for (int i=0;i<count;i++) _lamps[i]=offers[_selected[i]];
        if (!_map.Render(device,grid,_lamps.AsSpan(0,count))) return false;
        _count=count; return true;
    }
    public int RowForOffer(int offer)
    {
        for (int i=0;i<_count;i++) if (_selected[i]==offer) return i;
        return -1;
    }
    public void Dispose() { if (_disposed) return; _disposed=true; _count=0; _map.Dispose(); _map.Texture.Dispose(); }
}
