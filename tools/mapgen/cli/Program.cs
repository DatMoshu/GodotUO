// guo-mapgen: command-line front end of the map generator. Contract: docs/data_formats.md,
// "Map generator CLI". Every command prints JSON to stdout; the generator's own console
// output goes to stderr so stdout stays machine-readable.
//
//   guo-mapgen schema  [--preset P]                     passes, tunables, presets
//   guo-mapgen run     --out DIR [--preset P] [--seed N] [--width W --height H | --size N]
//                      [--set "Pass.Key=value"]... [--disable "Pass"]... [--enable "Pass"]...
//                      [--fast] [--step-previews] [--preview-max N] [--client-data DIR]
//   guo-mapgen export  --run DIR [--facet N] [--client-data DIR]
//   guo-mapgen presets
//   guo-mapgen prepare [--dragon DIR [--out FILE]] [--landscaper DIR]   map-tool data from the user's own copies

using System.Text.Json;
using GuoMapGen;

var stdout = Console.Out;
Console.SetOut(Console.Error);
var json = new JsonEmitter(stdout);

try
{
    var a = Args.Parse(args);
    switch (a.Command)
    {
        case "schema":
            json.Write(SchemaCommand.Build(a.Get("preset")));
            return 0;
        case "presets":
            json.Write(new Dictionary<string, object?> { ["schema"] = "guo.mapgen.presets/1", ["presets"] = Presets.List() });
            return 0;
        case "run":
            return RunCommand.Execute(a, json);
        case "export":
            return ExportCommand.Execute(a, json);
        case "prepare":
            return PrepareCommand.Execute(a, json);
        default:
            json.Event("error", new() { ["message"] = $"unknown command '{a.Command}'. Commands: schema, presets, run, export, prepare" });
            return 2;
    }
}
catch (CliError e)
{
    json.Event("error", new() { ["message"] = e.Message });
    return 2;
}
catch (Exception e)
{
    json.Event("error", new() { ["message"] = e.Message, ["type"] = e.GetType().Name });
    Console.Error.WriteLine(e);
    return 1;
}

namespace GuoMapGen
{
    /// <summary>A user error: bad arguments or an unusable output folder. Exit code 2.</summary>
    public sealed class CliError(string message) : Exception(message);

    /// <summary>Writes one compact JSON value per line to the real stdout.</summary>
    public sealed class JsonEmitter(TextWriter writer)
    {
        public static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

        public void Write(object value)
        {
            writer.WriteLine(JsonSerializer.Serialize(value, Options));
            writer.Flush();
        }

        public void Event(string name, Dictionary<string, object?> fields)
        {
            var e = new Dictionary<string, object?> { ["event"] = name };
            foreach (var (k, v) in fields) e[k] = v;
            Write(e);
        }
    }

    /// <summary>"command --key value --flag --list a --list b". Repeated keys accumulate.</summary>
    public sealed class Args
    {
        public string Command { get; private init; } = "";
        private readonly Dictionary<string, List<string>> _values = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> FlagNames = new(StringComparer.OrdinalIgnoreCase)
            { "fast", "step-previews", "progress-json" };

        public static Args Parse(string[] argv)
        {
            if (argv.Length == 0) throw new CliError("no command. Commands: schema, presets, run, export, prepare");
            var a = new Args { Command = argv[0].ToLowerInvariant() };
            for (int i = 1; i < argv.Length; i++)
            {
                var t = argv[i];
                if (!t.StartsWith("--")) throw new CliError($"unexpected argument '{t}'");
                var key = t[2..];
                if (FlagNames.Contains(key)) { a._flags.Add(key); continue; }
                if (i + 1 >= argv.Length) throw new CliError($"--{key} needs a value");
                if (!a._values.TryGetValue(key, out var list)) a._values[key] = list = new List<string>();
                list.Add(argv[++i]);
            }
            return a;
        }

        public string? Get(string key) => _values.TryGetValue(key, out var l) ? l[^1] : null;
        public IReadOnlyList<string> All(string key) => _values.TryGetValue(key, out var l) ? l : Array.Empty<string>();
        public bool Flag(string key) => _flags.Contains(key);

        public int Int(string key, int fallback)
        {
            var v = Get(key);
            if (v is null) return fallback;
            return int.TryParse(v, out var n) ? n : throw new CliError($"--{key} must be a whole number, got '{v}'");
        }

        public long? Long(string key)
        {
            var v = Get(key);
            if (v is null) return null;
            return long.TryParse(v, out var n) ? n : throw new CliError($"--{key} must be a whole number, got '{v}'");
        }
    }
}
