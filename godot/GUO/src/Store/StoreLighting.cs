// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using GUO.Renderer.PostFx;

namespace GUO.Store;

/// <summary>A mounted, hash-checked HD look. Installation never compiles shaders.</summary>
internal sealed class StoreLighting
{
    internal static readonly string[] ShaderNames = StorePack.LightingShaderNames;
    internal PostFxPreset Preset { get; private init; }
    internal IReadOnlyDictionary<string, string> Shaders { get; private init; }

    internal static StoreLighting Decode(StoreVerifiedPack pack, StoreComponent component)
    {
        StorePack.Validate(pack.Manifest);
        StorePack.ValidateLighting(pack.Manifest, path => pack.ReadPayload(path, StorePack.MaxLightingBytes));
        string folder = Path.GetDirectoryName(component.Entry)?.Replace('\\', '/') ?? "";
        string prefix = folder.Length == 0 ? "" : folder + "/";
        var preset = PostFxPreset.Parse(Encoding.UTF8.GetString(pack.ReadPayload(component.Entry, 65536)), component.Entry);
        StorePack.Require(preset.Passes.Count is > 0 and <= PostFxStack.MaxPasses, "Invalid lighting pass count");
        StorePack.Require(preset.Bindings.Count == 0, "HD lighting uses runtime weather and sun bindings");
        var shaders = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string name in ShaderNames)
            shaders.Add(name, StorePack.LightingText(pack.ReadPayload(prefix + name + ".gdshader", 65536)));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pass in preset.Passes)
        {
            StorePack.Require(shaders.ContainsKey(pass.Shader) && seen.Add(pass.Shader), "Unknown or duplicate HD lighting pass");
        }
        StorePack.Require(preset.Passes.FindAll(p => p.Enabled && p.Shader is "hd_tonemap" or "hd_night_vision").Count == 1,
            "HD lighting needs exactly one tonemap or night-vision composite");
        return new StoreLighting { Preset = preset, Shaders = shaders };
    }
}
