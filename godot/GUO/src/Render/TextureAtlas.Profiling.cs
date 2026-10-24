// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.Diagnostics;
using GUO.Utility;

namespace GUO.Renderer;

public sealed partial class TextureAtlas
{
    private sealed class PageObservation
    {
        internal int Atlas, Page, Width, Height;
        internal string Owner;
        internal long Updates, Creations, UpdateTicks, CreationTicks;
    }
    private static int _nextAtlasId;
    private readonly int _atlasId = ++_nextAtlasId;
    private List<PageObservation> _pageObservations;
    private static readonly List<PageObservation> _observedPages = new();
    internal static long ObservedUpdates, ObservedCreations;

    // Called before the allocation/time baseline. Retain observations even if
    // an atlas is disposed during the measured interval; no resource refs.
    internal static void BeginObservations()
    {
        ObservedUpdates = ObservedCreations = 0;
        _observedPages.Clear();
        foreach (TextureAtlas atlas in _live)
        {
            atlas._pageObservations?.Clear();
            atlas.EnsureObservations();
        }
    }
    private void EnsureObservations()
    {
        _pageObservations ??= new List<PageObservation>();
        while (_pageObservations.Count < _textures.Count)
        {
            int index = _pageObservations.Count;
            PageMetadata size = _metadata[_textures[index]];
            var page = new PageObservation { Atlas = _atlasId, Page = index,
                Width = size.Width, Height = size.Height, Owner = _diagnosticOwner };
            _pageObservations.Add(page);
            _observedPages.Add(page);
        }
    }
    private void ObserveUpload(int index, long ticks, bool creation)
    {
        if (!RuntimeTimings.Enabled) return;
        EnsureObservations();
        PageObservation page = _pageObservations[index];
        if (creation)
        {
            page.Creations++; page.CreationTicks += ticks; ObservedCreations++;
        }
        else
        {
            page.Updates++; page.UpdateTicks += ticks; ObservedUpdates++;
        }
    }
    internal static List<Dictionary<string, object>> UploadReport()
    {
        var result = new List<Dictionary<string, object>>();
        foreach (PageObservation p in _observedPages)
        {
            result.Add(new Dictionary<string, object>
            {
                ["atlas"] = p.Atlas, ["page"] = p.Page, ["owner"] = p.Owner,
                ["width"] = p.Width, ["height"] = p.Height,
                ["updates"] = p.Updates, ["creations"] = p.Creations,
                ["rgba_bytes_submitted"] = (p.Updates + p.Creations) * p.Width * p.Height * 4L,
                ["update_call_ms"] = Math.Round(p.UpdateTicks * 1000.0 / Stopwatch.Frequency, 4),
                ["creation_call_ms"] = Math.Round(p.CreationTicks * 1000.0 / Stopwatch.Frequency, 4),
            });
        }
        return result;
    }
}
