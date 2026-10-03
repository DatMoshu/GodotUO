#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

/// <summary>What a chat provider needs to offer tools to a model: their specs, and a way to run one.</summary>
public interface IChatTools
{
    /// <summary>The tools as OpenAI-style function specs (Ollama takes the same shape).</summary>
    JsonArray Specs();

    /// <summary>Runs one tool call and returns its result as text for the model. Never throws for a bad call: the text says why.</summary>
    Task<string> RunAsync(string name, JsonNode args, CancellationToken ct);
}

/// <summary>
/// The editor's tools for models that can call functions (ADR-0028). Today all of them read:
/// <c>search</c> (the F3 index), <c>inspect_asset</c> (the UO Inspector's text for an asset) and
/// <c>jump_world</c> (move the World tab; navigation, not an edit). A tool that changes anything
/// is registered with <c>ReadOnly = false</c> and then runs only after <see cref="Approve"/> says
/// the user agreed in a dialog; with no approver it is refused. Tools run on the main thread (the
/// loaders and the docks are not thread safe) and a model's call waits for it.
/// </summary>
public sealed class AiToolHost : IChatTools
{
    public sealed class Tool
    {
        public string Name = "";
        public string Description = "";
        public JsonObject Parameters = new() { ["type"] = "object", ["properties"] = new JsonObject() };

        /// <summary>False for anything that changes something: it needs the user's approval each time.</summary>
        public bool ReadOnly = true;

        /// <summary>Runs on the main thread.</summary>
        public Func<JsonNode, string> Run = _ => "";
    }

    private static readonly Regex Bb = new(@"\[/?[a-z_]+(?:=[^\]]*)?\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private const int MaxResult = 3000;

    private readonly List<Tool> _tools = new();
    private readonly Action<Action> _post;
    private readonly List<string> _calls = new();

    /// <param name="post">Queues work for the main thread (<see cref="AiHub.Post"/>).</param>
    public AiToolHost(Action<Action> post)
    {
        _post = post;
    }

    /// <summary>Asks the user whether a changing tool may run (a dialog). Null: none may.</summary>
    public Func<string, Task<bool>> Approve { get; set; }

    /// <summary>The tool calls made so far, "name {args}", for the transcript and the smoke.</summary>
    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_calls)
            {
                return _calls.ToArray();
            }
        }
    }

    public IReadOnlyList<Tool> Tools => _tools;

    public void Register(Tool t) => _tools.Add(t);

    public JsonArray Specs()
    {
        var a = new JsonArray();
        foreach (Tool t in _tools)
        {
            a.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = JsonNode.Parse(t.Parameters.ToJsonString()),
                },
            });
        }

        return a;
    }

    public async Task<string> RunAsync(string name, JsonNode args, CancellationToken ct)
    {
        lock (_calls)
        {
            _calls.Add($"{name} {args?.ToJsonString() ?? "{}"}");
        }

        Tool t = _tools.Find(x => x.Name == name);
        if (t == null)
        {
            return $"error: no tool named {name}. Tools: {string.Join(", ", _tools.Select(x => x.Name))}";
        }

        if (!t.ReadOnly)
        {
            Func<string, Task<bool>> ask = Approve;
            bool yes = ask != null && await ask($"{t.Name} {args?.ToJsonString()}\n\n{t.Description}").ConfigureAwait(false);
            if (!yes)
            {
                return "refused: the user did not approve this action";
            }
        }

        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _post(() =>
        {
            try
            {
                done.TrySetResult(t.Run(args));
            }
            catch (Exception ex)
            {
                done.TrySetResult($"error: {ex.Message}");
            }
        });
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using (timeout.Token.Register(() => done.TrySetResult("error: the editor did not answer in time")))
        {
            string r = await done.Task.ConfigureAwait(false);
            return r.Length > MaxResult ? r[..MaxResult] + "\n...(cut)" : r;
        }
    }

    /// <summary>The three read-only tools, wired to the editor's index, panels and World tab.</summary>
    internal static AiToolHost For(SearchContext ctx, Func<SearchIndex> index, Action<Action> post)
    {
        var host = new AiToolHost(post);
        host.Register(new Tool
        {
            Name = "search",
            Description = "Search the UO editor's index (assets by name or id, hues, places, cliloc text, menus, settings). Returns lines of 'kind | title | hint'.",
            Parameters = Params(("query", "string", "what to look for, e.g. 'backpack', '0x0E75', 'britain bank'")),
            Run = a => Search(index?.Invoke(), (string)a?["query"]),
        });
        host.Register(new Tool
        {
            Name = "inspect_asset",
            Description = "Select an asset in the UO Assets tab and return the text the UO Inspector shows for it (size, flags, ids). 'panel' is the tab name (Art, Statics, Gumps, Hues, ...); leave it empty to list them.",
            Parameters = Params(("panel", "string", "the Assets tab, e.g. Statics"), ("query", "string", "a name or id to select there, e.g. 0x0E75")),
            Run = a => Inspect(ctx, (string)a?["panel"], (string)a?["query"]),
        });
        host.Register(new Tool
        {
            Name = "jump_world",
            Description = "Move the World tab's camera to a map cell. Read-only navigation: nothing is edited.",
            Parameters = Params(("x", "integer", "map x"), ("y", "integer", "map y"), ("facet", "integer", "map number, default 0")),
            Run = a => Jump(ctx, a),
        });
        return host;
    }

    private static JsonObject Params(params (string Name, string Type, string Doc)[] props)
    {
        var p = new JsonObject();
        foreach (var (n, t, d) in props)
        {
            p[n] = new JsonObject { ["type"] = t, ["description"] = d };
        }

        return new JsonObject { ["type"] = "object", ["properties"] = p };
    }

    private static string Search(SearchIndex index, string query)
    {
        if (index == null)
        {
            return "error: the search index is not available";
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return "error: give a query";
        }

        if (!index.Ready)
        {
            return $"the index is still building ({index.Progress:P0}; waiting on {index.Pending}); try again shortly";
        }

        var sb = new StringBuilder();
        int n = 0;
        foreach (SearchGroup g in index.Query(query))
        {
            foreach (var (e, _) in g.Items)
            {
                sb.Append(e.Kind).Append(" | ").Append(e.Title);
                if (e.Hint.Length > 0)
                {
                    sb.Append(" | ").Append(e.Hint);
                }

                if (e.Target is var (f, x, y))
                {
                    sb.Append($" | map{f} {x},{y}");
                }

                sb.Append('\n');
                if (++n >= 15)
                {
                    return sb.ToString();
                }
            }
        }

        return n == 0 ? "no matches" : sb.ToString();
    }

    private static string Inspect(SearchContext ctx, string panel, string query)
    {
        if (ctx?.Assets == null)
        {
            return "error: the Assets tab is not available";
        }

        string names = string.Join(", ", ctx.Assets.Panels.Select(p => p.Name.ToString()));
        if (string.IsNullOrWhiteSpace(panel))
        {
            return "Assets tabs: " + names;
        }

        AssetPanel found = ctx.Assets.Panels.FirstOrDefault(p => string.Equals(p.Name.ToString(), panel.Trim(), StringComparison.OrdinalIgnoreCase));
        if (found == null)
        {
            return $"error: no Assets tab named {panel}. Tabs: {names}";
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return "error: give a query (a name or id) to select in that tab";
        }

        if (!ctx.Data.IsLoaded)
        {
            return "error: the client data is not loaded yet";
        }

        if (!ctx.OpenAsset(found, query))
        {
            return $"nothing in {panel} matches '{query}'";
        }

        Inspection i = ctx.Inspector?.Current;
        if (i == null)
        {
            return "selected, but the inspector shows nothing";
        }

        string text = Bb.Replace(i.Text ?? "", "").Replace("[lb]", "[");
        return $"{i.Source} {i.Id}" + (i.Image != null ? $" (image {i.Image.GetWidth()}x{i.Image.GetHeight()}, {i.Frames.Length} frame(s))" : "") + "\n" + text;
    }

    private static string Jump(SearchContext ctx, JsonNode a)
    {
        if (ctx?.ShowInWorld == null)
        {
            return "error: the World tab is not available";
        }

        if (!TryInt(a?["x"], out int x) || !TryInt(a?["y"], out int y))
        {
            return "error: x and y are required integers";
        }

        int facet = TryInt(a?["facet"], out int f) ? f : 0;
        if (x < 0 || y < 0 || x > 8191 || y > 8191 || facet < 0 || facet > 5)
        {
            return "error: x and y must be 0..8191 and facet 0..5";
        }

        ctx.ShowInWorld(facet, x, y);
        return $"the World tab now shows map{facet} {x},{y}";
    }

    private static bool TryInt(JsonNode n, out int v)
    {
        v = 0;
        if (n is JsonValue jv)
        {
            if (jv.TryGetValue(out int i))
            {
                v = i;
                return true;
            }

            if (jv.TryGetValue(out double d))
            {
                v = (int)d;
                return true;
            }

            if (jv.TryGetValue(out string s) && int.TryParse(s, out int p))
            {
                v = p;
                return true;
            }
        }

        return false;
    }
}
#endif
