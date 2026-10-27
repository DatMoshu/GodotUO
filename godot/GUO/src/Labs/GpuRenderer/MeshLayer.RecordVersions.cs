// SPDX-License-Identifier: BSD-2-Clause
#if GUO_LABS
using System;

namespace GUO.Renderer;

public sealed partial class MeshLayer
{
    private long[] _recordVersions;
    private long _recordSequence, _recordResetVersion;

    // Allocated only after this layer participates in the opt-in capture.
    // Visibility/order do not change quad content. A pooled reset invalidates
    // every index even if the next chunk writes its arrays directly.
    internal long QuadRecordVersion(int index)
    {
        _recordVersions ??= new long[Vertices.Length];
        if (_recordVersions.Length < Vertices.Length)
            Array.Resize(ref _recordVersions, Vertices.Length);
        return Math.Max(_recordResetVersion, _recordVersions[index]);
    }

    partial void RecordVertexChanged(int index)
    {
        if (_recordVersions == null) return;
        if (_recordVersions.Length < Vertices.Length)
            Array.Resize(ref _recordVersions, Vertices.Length);
        _recordVersions[index] = checked(++_recordSequence);
    }

    partial void RecordLayerReset()
    {
        // ADR-0044: a pooled layer can return with the same quad count but
        // entirely different geometry/textures, before per-quad IDs exist.
        ContentStamp++;
        if (_recordVersions != null) _recordResetVersion = checked(++_recordSequence);
    }
}
#endif
