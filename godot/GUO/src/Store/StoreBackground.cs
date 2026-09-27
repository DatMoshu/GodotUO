// SPDX-License-Identifier: BSD-2-Clause
using System;

namespace GUO.Store;

internal static class StoreBackground
{
    // Include the final slash: removing 1.0.1 must not match 1.0.10.
    public static bool BelongsTo(string path, string id, string version) =>
        path != null && path.Replace('\\', '/').StartsWith(
            $"user://store/{id}/{version}/", StringComparison.OrdinalIgnoreCase);
}
