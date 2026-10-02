// SPDX-License-Identifier: BSD-2-Clause
using System.IO;
using System.Text.Json;

namespace GUO.Store;

internal static class StoreTileDefinition
{
    public static void Validate(JsonElement content)
    {
        foreach (var property in content.EnumerateObject())
        {
            switch (property.Name)
            {
                case "name":
                    string name = property.Value.GetString();
                    if (name == null || name.Length > 20) throw new InvalidDataException("Invalid tile name");
                    break;
                case "flags": _ = property.Value.GetUInt64(); break;
                case "height": case "weight": case "layer": _ = property.Value.GetByte(); break;
                case "animation": case "light": _ = property.Value.GetUInt16(); break;
                default: throw new InvalidDataException("Unknown tile metadata field: " + property.Name);
            }
        }
    }
}
