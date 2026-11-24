using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CentrED.MapGen.Paint;
using CentrED.MapGen.Passes.Terrain;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;

namespace GuoMapGen;

// A completed run owns its inputs. Export can replay it after the artist edits the original pair.
public sealed record PaintedDraft(
    [property: JsonPropertyName("terrain_name")] string Name,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("terrain_sha256")] string TerrainSha256,
    [property: JsonPropertyName("altitude_sha256")] string AltitudeSha256,
    [property: JsonPropertyName("min_z")] int MinZ,
    [property: JsonPropertyName("max_z")] int MaxZ)
{
    [JsonIgnore] public string InputPrefix { get; private init; } = "";
    private static string Prefix(string run) => Path.GetFullPath(Path.Combine(run, "draft", "input"));

    internal static PaintedDraft Capture(string file, StagedFolder run)
    {
        const string suffix = ".terrain.png";
        if (!file.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            throw new CliError("Choose NAME.terrain.png and put NAME.altitude.png beside it.");
        string source = Path.GetFullPath(file);
        string altitude = PaintPngPair.AltitudePath(source[..^suffix.Length]);
        if (!File.Exists(source) || !File.Exists(altitude))
            throw new CliError($"Painted draft needs both {Path.GetFileName(source)} and {Path.GetFileName(altitude)}.");
        using var terrainFile = File.OpenRead(source);
        using var altitudeFile = File.OpenRead(altitude);
        var terrainSize = Identify(terrainFile, source);
        var altitudeSize = Identify(altitudeFile, altitude);
        if (terrainSize != altitudeSize)
            throw new CliError($"Painted draft dimensions differ: terrain {terrainSize.Width} x {terrainSize.Height}, altitude {altitudeSize.Width} x {altitudeSize.Height}.");
        RunCommand.CheckSize(terrainSize.Width, terrainSize.Height);
        string prefix = Prefix(run.WorkingPath);
        Directory.CreateDirectory(Path.GetDirectoryName(prefix)!);
        string terrainHash = Copy(terrainFile, PaintPngPair.TerrainPath(prefix));
        string altitudeHash = Copy(altitudeFile, PaintPngPair.AltitudePath(prefix));
        var mapping = new ImageImportParams();
        return new(Path.GetFileName(source), terrainSize.Width, terrainSize.Height, terrainHash, altitudeHash,
            mapping.MinZ, mapping.MaxZ) { InputPrefix = Prefix(run.Destination) };
    }

    private static (int Width, int Height) Identify(Stream input, string path)
    {
        try
        {
            var info = Image.Identify(input);
            if (info.Metadata.DecodedImageFormat is not PngFormat || info.FrameMetadataCollection.Count > 1)
                throw new CliError($"Choose a single-frame PNG for {Path.GetFileName(path)}.");
            input.Position = 0;
            return (info.Width, info.Height);
        }
        catch (Exception e) when (e is UnknownImageFormatException or InvalidImageContentException)
        { throw new CliError($"Cannot read {Path.GetFileName(path)}. Save a valid PNG and retry."); }
    }

    private static string Copy(Stream input, string path)
    {
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        input.CopyTo(output);
        output.Position = 0;
        return Convert.ToHexString(SHA256.HashData(output)).ToLowerInvariant();
    }

    public static PaintedDraft? Verify(JsonElement manifest, string run)
    {
        if (!manifest.TryGetProperty("painted_draft", out var value)) return null;
        var draft = value.Deserialize<PaintedDraft>() ?? throw new CliError("Invalid painted draft manifest.");
        if (draft.Width != manifest.GetProperty("width").GetInt32() || draft.Height != manifest.GetProperty("height").GetInt32()
            || draft.MinZ < -128 || draft.MaxZ > 127 || draft.MinZ >= draft.MaxZ)
            throw new CliError("Invalid painted draft dimensions or elevation range in run.json.");
        string prefix = Prefix(run);
        foreach (var (path, hash) in new[] { (PaintPngPair.TerrainPath(prefix), draft.TerrainSha256),
                                             (PaintPngPair.AltitudePath(prefix), draft.AltitudeSha256) })
        {
            if (!File.Exists(path)) throw new CliError($"Missing retained draft {Path.GetFileName(path)}. Generate the pair again.");
            using var stream = File.OpenRead(path);
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new CliError($"Retained draft {Path.GetFileName(path)} changed. Generate a new run.");
        }
        return draft with { InputPrefix = prefix };
    }
}
