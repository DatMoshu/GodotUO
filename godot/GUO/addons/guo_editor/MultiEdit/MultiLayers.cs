#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>Editor visibility does not change native component flags or export membership.</summary>
public sealed record MultiLayer(int Id, string Name, bool Visible = true, bool Locked = false);

public sealed partial class MultiDocument
{
    public List<MultiLayer> Layers { get; private set; } = new();
    // Immutable source provenance; these masks are not live constraints after edits.
    public string GenerationJson { get; private set; }
    public void LoadGeneration(string json) => GenerationJson = json;
    public int ActiveLayerId { get; private set; }
    public string LastEditError { get; private set; }
    public bool LayerVisible(int id) => Layers.FirstOrDefault(l => l.Id == id)?.Visible ?? true;

    // Manual controls and AI tools use this same revision-checked operation.
    // Preview validates without altering selection, history, IDs or document revision.
    public object EditLayer(long revision, string action, int layerId, string name = "",
        int dx = 0, int dy = 0, int dz = 0, int tile = -1, int replacement = -1,
        bool value = false, bool apply = true)
    {
        if (revision != Revision) throw new InvalidOperationException("Document changed. Read multi_document and preview again.");
        var layer = Layers.FirstOrDefault(l => l.Id == layerId);
        if (action != "create" && layer == null) throw new ArgumentException("Unknown layer.");
        if (!new[] { "create", "rename", "visible", "lock", "activate", "select", "assign", "move", "replace" }.Contains(action))
            throw new ArgumentException("Unknown layer action.");
        if (action is "create" or "rename" && (string.IsNullOrWhiteSpace(name) || name.Length > 80))
            throw new ArgumentException("Layer name must contain 1–80 characters.");
        var targets = (action == "assign" ? Parts.Where(p => Selection.Contains(p.Uid)) : Parts.Where(p => p.LayerId == layerId)).ToList();
        if (action is "assign" or "move" or "replace")
        {
            if (layer.Locked || targets.Any(p => Layers.Any(l => l.Id == p.LayerId && l.Locked)))
                throw new InvalidOperationException("Unlock the affected layer before editing.");
            if (action == "move" && targets.Any(p => (long)p.X + dx < short.MinValue || (long)p.X + dx > short.MaxValue
                || (long)p.Y + dy < short.MinValue || (long)p.Y + dy > short.MaxValue
                || (long)p.Z + dz < short.MinValue || (long)p.Z + dz > short.MaxValue))
                throw new ArgumentException("Move exceeds native coordinate range.");
            if (action == "replace" && (tile < 0 || tile > ushort.MaxValue || replacement < 0 || replacement > ushort.MaxValue))
                throw new ArgumentException("Tile IDs must be 0–65535.");
        }
        int affected = action == "replace" ? targets.Count(p => p.Id == tile) : targets.Count;
        if (action == "create" && Layers.Count >= 256) throw new InvalidOperationException("Layer limit is 256.");
        int createdId = 0;
        if (action == "create")
        {
            int highestId = Layers.Max(l => l.Id);
            if (highestId >= int.MaxValue - 1) throw new InvalidOperationException("Layer identifiers exhausted.");
            createdId = highestId + 1;
        }
        if (apply)
        {
            if (action == "activate") { ActiveLayerId = layerId; Revision++; Changed?.Invoke(); }
            else if (action == "select")
            {
                Selection.Clear();
                foreach (var p in targets) Selection.Add(p.Uid);
                Revision++; Changed?.Invoke();
            }
            else
            {
                bool changed = Do($"{action} layer {(layer?.Name ?? name)}", parts =>
                {
                    if (action == "create") Layers.Add(new MultiLayer(createdId, name.Trim()));
                    else if (action is "rename" or "visible" or "lock")
                    {
                        int i = Layers.FindIndex(l => l.Id == layerId);
                        Layers[i] = action == "rename" ? layer with { Name = name.Trim() }
                            : action == "visible" ? layer with { Visible = value } : layer with { Locked = value };
                    }
                    else
                    {
                        var ids = targets.Select(p => p.Uid).ToHashSet();
                        for (int i = 0; i < parts.Count; i++)
                        {
                            var p = parts[i];
                            if (!ids.Contains(p.Uid)) continue;
                            if (action == "assign") p.LayerId = layerId;
                            if (action == "move") { p.X = (short)(p.X + dx); p.Y = (short)(p.Y + dy); p.Z = (short)(p.Z + dz); }
                            if (action == "replace" && p.Id == tile) p.Id = (ushort)replacement;
                            parts[i] = p;
                        }
                    }
                });
                if (!changed && LastEditError != null) throw new InvalidOperationException(LastEditError);
                if (action == "create") { ActiveLayerId = createdId; Changed?.Invoke(); }
            }
        }
        return new { action, applied = apply, revision = Revision, layerId = action == "create" ? createdId : layerId, affected };
    }
}

/// <summary>Optional authoring sidecar inside format-1 component descriptions; native writers still consume components.</summary>
internal static class MultiLayerCodec
{
    public static string Encode(MultiDocument doc, string componentsJson)
    {
        var root = JsonNode.Parse(componentsJson).AsObject();
        if (doc.GenerationJson != null) root["generation"] = JsonNode.Parse(doc.GenerationJson);
        root["editor_layers"] = JsonSerializer.SerializeToNode(new
        {
            version = 1,
            layers = doc.Layers,
            membership = doc.Parts.Select(p => p.LayerId).ToArray()
        });
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public static string ReadGeneration(string json)
    {
        var node = JsonNode.Parse(json)["generation"];
        if (node != null && node is not JsonObject) throw new ArgumentException("Generation provenance must be an object.");
        return node?.ToJsonString();
    }

    public static List<MultiLayer> Decode(string json, List<MultiPart> parts)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new ArgumentException("Expected a component description object.");
        if (!root.TryGetPropertyValue("editor_layers", out var value)) return new() { new MultiLayer(0, "Components") };
        if (value is not JsonObject meta) throw new ArgumentException("Invalid editor layer metadata.");
        if ((int?)meta["version"] != 1) throw new ArgumentException("Unsupported editor layer version.");
        var layers = meta["layers"]?.Deserialize<List<MultiLayer>>() ?? throw new ArgumentException("Missing layers.");
        var membership = meta["membership"]?.Deserialize<int[]>() ?? throw new ArgumentException("Missing layer membership.");
        if (layers.Count == 0 || layers.Count > 256 || layers.Any(l => l == null) || layers.Select(l => l.Id).Distinct().Count() != layers.Count
            || layers.Any(l => l.Id < 0 || l.Id == int.MaxValue || string.IsNullOrWhiteSpace(l.Name) || l.Name.Length > 80)
            || membership.Length != parts.Count || membership.Any(id => !layers.Any(l => l.Id == id)))
            throw new ArgumentException("Invalid layers or component membership.");
        for (int i = 0; i < parts.Count; i++) { var p = parts[i]; p.LayerId = membership[i]; parts[i] = p; }
        return layers;
    }
}
#endif
