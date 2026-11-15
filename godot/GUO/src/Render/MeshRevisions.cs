// SPDX-License-Identifier: BSD-2-Clause
using System.Collections.Generic;
using Godot;

namespace GUO.Renderer;

/// <summary>
/// Content revisions of meshes that are rebuilt in place under the same RID (MergedLand,
/// CoverRuns, GlyphMesh pools). Retained canvas commands (UltimaBatcher2D.RetainedCanvas)
/// treat a mesh command as unchanged only when its revision matches; a mesh no builder
/// stamps stays at revision 0, compared by RID as before. Without this a rebuilt land mesh kept the
/// canvas item's old command and bounds, and the land of travelled-to chunks drew black
/// (Deck endurance, 2026-11-14). Main-thread only.
/// </summary>
internal static class MeshRevisions
{
    private static readonly Dictionary<Rid, int> Revisions = new();

    /// <summary>The mesh's contents changed; call after rebuilding its surfaces.</summary>
    internal static void Bump(Rid mesh)
    {
        Revisions.TryGetValue(mesh, out int revision);
        Revisions[mesh] = revision == int.MaxValue ? 1 : revision + 1;
    }

    /// <summary>The mesh's current revision, or 0 when no builder stamps it.</summary>
    internal static int Get(Rid mesh) => Revisions.TryGetValue(mesh, out int revision) ? revision : 0;
}
