#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

/// <summary>
/// Where an imported image came from (ADR-0029, docs/data_formats.md section 11):
/// <c>{tool, model?, workflow?, seed?, inputs, derived_from_client_art}</c>.
/// <see cref="DerivedFromClientArt"/> is true when any input was the client's own art;
/// such an image stays local and the store's content policy refuses it.
/// <see cref="Kind"/> is "" for images (the original records), "audio" or "model"
/// for ComfyUI audio/3D artifacts; absent reads back as "".
/// </summary>
public sealed class ArtProvenance
{
    public string Tool = "";
    public string Model;
    public string Workflow;
    public long? Seed;
    public List<string> Inputs = new();
    public bool DerivedFromClientArt;

    /// <summary>"", "audio" or "model". Empty for every record written before kinds existed.</summary>
    public string Kind = "";

    public JsonObject ToJson()
    {
        var o = new JsonObject { ["tool"] = Tool };
        if (!string.IsNullOrEmpty(Model))
        {
            o["model"] = Model;
        }

        if (!string.IsNullOrEmpty(Workflow))
        {
            o["workflow"] = Workflow;
        }

        if (!string.IsNullOrEmpty(Kind))
        {
            o["kind"] = Kind;
        }

        if (Seed.HasValue)
        {
            o["seed"] = Seed.Value;
        }

        var inputs = new JsonArray();
        foreach (string i in Inputs)
        {
            inputs.Add(i);
        }

        o["inputs"] = inputs;
        o["derived_from_client_art"] = DerivedFromClientArt;
        return o;
    }

    public static ArtProvenance FromJson(JsonNode n)
    {
        var p = new ArtProvenance
        {
            Tool = (string)n?["tool"] ?? "",
            Model = (string)n?["model"],
            Workflow = (string)n?["workflow"],
            Kind = (string)n?["kind"] ?? "",
            Seed = n?["seed"] is JsonNode s ? (long?)s : null,
            DerivedFromClientArt = n?["derived_from_client_art"] is JsonNode d && (bool)d,
        };
        if (n?["inputs"] is JsonArray a)
        {
            p.Inputs = a.Select(x => (string)x ?? "").ToList();
        }

        return p;
    }
}

/// <summary>
/// The provenance file of a world project's asset overlay: <c>assets/provenance.json</c>,
/// one entry per replaced image, keyed by its path in the project. Written next to the
/// images, never inside the client install.
/// </summary>
public sealed class AssetProvenance
{
    public const int Format = 1;

    private readonly string _path;

    public AssetProvenance(AssetOverlay overlay)
    {
        _path = Path.Combine(overlay.Root, "assets", "provenance.json");
    }

    private JsonObject Read()
    {
        try
        {
            if (File.Exists(_path) && JsonNode.Parse(File.ReadAllText(_path)) is JsonObject o && o["entries"] is JsonObject)
            {
                return o;
            }
        }
        catch (Exception)
        {
            // A damaged file is replaced rather than blocking an import.
        }

        return new JsonObject { ["format"] = Format, ["entries"] = new JsonObject() };
    }

    public void Record(string assetPath, ArtProvenance p)
    {
        JsonObject root = Read();
        JsonObject entry = p.ToJson();
        entry["imported"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        ((JsonObject)root["entries"])[assetPath] = entry;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    public ArtProvenance Get(string assetPath) =>
        Read()["entries"] is JsonObject e && e[assetPath] is JsonNode n ? ArtProvenance.FromJson(n) : null;

    public void Remove(string assetPath)
    {
        JsonObject root = Read();
        if (((JsonObject)root["entries"]).Remove(assetPath))
        {
            File.WriteAllText(_path, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
    }

    /// <summary>Every project path whose image is derived from client art: what may not be published.</summary>
    public List<string> Derived() =>
        Read()["entries"] is JsonObject e
            ? e.Where(kv => kv.Value is JsonObject o && o["derived_from_client_art"] is JsonNode d && (bool)d).Select(kv => kv.Key).OrderBy(k => k).ToList()
            : new List<string>();
}
#endif
