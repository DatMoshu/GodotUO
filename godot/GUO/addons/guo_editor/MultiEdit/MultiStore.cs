#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using GUO.Assets;
using GUO.IO;
using GUO.Utility;

/// <summary>What a save to a stage did.</summary>
public sealed class SaveResult
{
    public bool Ok;
    public string Name;
    public int? Id;
    public string Error;
    public string Log = "";
    public bool ReadBackEqual;
    public int Components;
}

/// <summary>
/// The Multi Editor's files (ADR-0031, data_formats §28) and its way into a stage. Writing never touches the
/// install: the built form goes to build/multi/built/ and <c>tools/multi write</c> puts it in the stage
/// (which refuses a folder inside the install). The stage is then read back with the ported UOP reader.
/// </summary>
internal static class MultiStore
{
    public static string EditDir => Path.Combine(EditorData.RepoRoot, "build", "multi", "edit");
    public static string BuiltDir(string name) => Path.Combine(EditorData.RepoRoot, "build", "multi", "built", name);
    public static string DefaultStage => Path.Combine(EditorData.RepoRoot, "build", "uodata", "multi");

    public static string SafeName(string name)
    {
        var sb = new StringBuilder();
        foreach (char c in (name ?? "").Trim())
        {
            sb.Append(char.IsLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_');
        }

        return sb.Length == 0 ? "multi" : sb.ToString();
    }

    // --- the components description (section 28) ----------------------------------

    public static string ToJson(string name, int? source, IReadOnlyList<MultiPart> parts)
    {
        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append("  \"format\": 1,\n  \"kind\": \"components\",\n");
        sb.Append($"  \"name\": {JsonSerializer.Serialize(name)},\n");
        sb.Append($"  \"source\": {(source.HasValue ? source.Value.ToString(CultureInfo.InvariantCulture) : "null")},\n");
        sb.Append($"  \"floor_z\": {Stories.FloorZ},\n  \"storey_height\": {Stories.Height},\n");
        sb.Append("  \"components\": [\n");
        for (int i = 0; i < parts.Count; i++)
        {
            MultiPart p = parts[i];
            sb.Append($"    [{p.Id}, {p.X}, {p.Y}, {p.Z}, {(p.Shown ? 1 : 0)}, {p.Hue}]{(i + 1 < parts.Count ? "," : "")}\n");
        }

        sb.Append("  ]\n}\n");
        return sb.ToString();
    }

    public static (string Name, int? Source, List<MultiPart> Parts) FromJson(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        if (root.GetProperty("kind").GetString() != "components")
        {
            throw new InvalidDataException("not a components description (kind must be \"components\")");
        }

        var parts = new List<MultiPart>();
        foreach (JsonElement c in root.GetProperty("components").EnumerateArray())
        {
            parts.Add(new MultiPart
            {
                Id = (ushort)c[0].GetInt32(),
                X = (short)c[1].GetInt32(),
                Y = (short)c[2].GetInt32(),
                Z = (short)c[3].GetInt32(),
                Shown = c.GetArrayLength() < 5 || c[4].GetInt32() != 0,
                Hue = c.GetArrayLength() > 5 ? (ushort)c[5].GetInt32() : (ushort)0,
            });
        }

        int? source = root.TryGetProperty("source", out JsonElement s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : null;
        return (root.GetProperty("name").GetString(), source, parts);
    }

    public static string SaveDescription(string name, int? source, IReadOnlyList<MultiPart> parts)
    {
        Directory.CreateDirectory(EditDir);
        string path = Path.Combine(EditDir, SafeName(name) + ".multi.json");
        File.WriteAllText(path, ToJson(name, source, parts), new UTF8Encoding(false));
        return path;
    }

    // --- the stage ---------------------------------------------------------------------

    /// <summary>The stage's MultiCollection.uop (or multi.mul) read back: id to parts. Null when the stage has neither.</summary>
    public static List<MultiPart> ReadStaged(string stageDir, int id)
    {
        string uop = Path.Combine(stageDir, "MultiCollection.uop");
        if (File.Exists(uop))
        {
            using var file = new UOFileUop(uop, "build/multicollection/{0:D6}.bin");
            file.FillEntries();
            ref UOFileIndex entry = ref file.GetValidRefEntry(id);
            if (entry.Length <= 0)
            {
                return null;
            }

            file.Seek(entry.Offset, SeekOrigin.Begin);
            var buf = new byte[entry.Length];
            file.Read(buf);
            if (entry.CompressionFlag >= CompressionType.Zlib)
            {
                var d = new byte[entry.DecompressedLength];
                ZLib.Decompress(buf, d);
                buf = d;
            }

            var parts = new List<MultiPart>();
            int count = BitConverter.ToInt32(buf, 4), p = 8;
            for (int i = 0; i < count; i++)
            {
                ushort item = BitConverter.ToUInt16(buf, p);
                short x = BitConverter.ToInt16(buf, p + 2), y = BitConverter.ToInt16(buf, p + 4), z = BitConverter.ToInt16(buf, p + 6);
                ushort flags = BitConverter.ToUInt16(buf, p + 8);
                uint n = BitConverter.ToUInt32(buf, p + 10);
                p += 14 + 4 * (int)n;
                parts.Add(new MultiPart { Id = item, X = x, Y = y, Z = z, Shown = flags is 0 or 0x100 or 0x101 });
            }

            return parts;
        }

        return null;
    }

    public static bool SameComponents(IReadOnlyList<MultiPart> a, IReadOnlyList<MultiPart> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Id != b[i].Id || a[i].X != b[i].X || a[i].Y != b[i].Y || a[i].Z != b[i].Z || a[i].Shown != b[i].Shown)
            {
                return false;
            }
        }

        return true;
    }

    private static Dictionary<string, int> StagedIds(string stageDir)
    {
        var result = new Dictionary<string, int>();
        string path = Path.Combine(stageDir, "multis.json");
        if (File.Exists(path))
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (JsonProperty p in doc.RootElement.EnumerateObject())
            {
                result[p.Name] = p.Value.GetProperty("id").GetInt32();
            }
        }

        return result;
    }

    /// <summary>Every multi the stage has authored: name and id, from its multis.json.</summary>
    public static IReadOnlyDictionary<string, int> Staged(string stageDir) => StagedIds(stageDir);

    /// <summary>
    /// Writes the parts into the stage as a new multi (or refreshes the same one) and reads the stage back.
    /// The writer only adds, so a changed multi under a used name goes under the next free <c>name-N</c>.
    /// Runs on a worker; touches no Godot object.
    /// </summary>
    public static Task<SaveResult> SaveAsync(string name, IReadOnlyList<MultiPart> parts, StaticTiles[] tiles, string stageDir)
    {
        MultiPart[] snapshot = parts.ToArray();
        return Task.Run(() =>
        {
            var r = new SaveResult { Components = snapshot.Length };
            try
            {
                stageDir = Path.GetFullPath(stageDir);
                string client = EditorData.Setting("UO_CLIENT_DATA", "");
                if (client.Length > 0 && IsInside(Path.GetFullPath(client), stageDir))
                {
                    r.Error = "the stage is inside the client install; GUO never writes there";
                    return r;
                }

                string baseName = SafeName(name);
                var staged = StagedIds(stageDir);
                string use = baseName;
                for (int n = 2; staged.TryGetValue(use, out int existing); n++)
                {
                    List<MultiPart> back = ReadStaged(stageDir, existing);
                    if (back != null && SameComponents(back, snapshot))
                    {
                        break;
                    }

                    use = $"{baseName}-{n}";
                }

                r.Name = use;
                Directory.CreateDirectory(BuiltDir(use));
                File.WriteAllText(Path.Combine(BuiltDir(use), "components.json"),
                    "[" + string.Join(",", snapshot.Select(p => $"[{p.Id},{p.X},{p.Y},{p.Z}{(p.Shown ? "" : ",0")}]")) + "]", new UTF8Encoding(false));
                int w = snapshot.Length == 0 ? 0 : snapshot.Max(p => p.X) - snapshot.Min(p => p.X);
                int h = snapshot.Length == 0 ? 0 : snapshot.Max(p => p.Y) - snapshot.Min(p => p.Y);
                var storeys = snapshot.Select(p => Stories.StoryOf(p.Z)).Where(s => s >= 0).Distinct().OrderBy(s => s).Select(Stories.ZOf).ToList();
                var side = new Dictionary<string, object>
                {
                    ["name"] = use,
                    ["size"] = new[] { w, h },
                    ["storeys"] = storeys,
                    ["doors"] = Array.Empty<object>(),
                    ["valid"] = true,
                    ["problems"] = Array.Empty<string>(),
                    ["note"] = "written by the Multi Editor (ADR-0031)",
                };
                File.WriteAllText(Path.Combine(BuiltDir(use), "multi.json"), JsonSerializer.Serialize(side, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));

                (int code, string log) = RunPython(new[] { Path.Combine("tools", "multi", "run.py"), "write", use, "--stage", stageDir });
                r.Log = log;
                if (code != 0)
                {
                    r.Error = $"tools/multi write exited {code}: {log.Trim().Split('\n').LastOrDefault()}";
                    return r;
                }

                staged = StagedIds(stageDir);
                if (!staged.TryGetValue(use, out int id))
                {
                    r.Error = "the stage's multis.json has no entry after the write";
                    return r;
                }

                r.Id = id;
                List<MultiPart> read = ReadStaged(stageDir, id);
                r.ReadBackEqual = read != null && SameComponents(read, snapshot);
                r.Ok = r.ReadBackEqual;
                if (!r.Ok)
                {
                    r.Error = "the stage read back different components";
                }
            }
            catch (Exception ex)
            {
                r.Error = $"{ex.GetType().Name}: {ex.Message}";
            }

            return r;
        });
    }

    private static bool IsInside(string parent, string child)
    {
        string p = parent.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return (child.TrimEnd('\\', '/') + Path.DirectorySeparatorChar).StartsWith(p, StringComparison.OrdinalIgnoreCase);
    }

    private static (int Code, string Log) RunPython(IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(EditorData.Setting("UO_PYTHON", OperatingSystem.IsWindows() ? "python" : "python3"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = EditorData.RepoRoot,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi);
        Task<string> err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output + err.Result);
    }
}
#endif
