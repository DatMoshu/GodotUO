// SF7: the client's argument trace never carries a credential.
// Run: dotnet run --project tools\arg_trace_tests
using GUO;
using System.Text.RegularExpressions;

int passed = 0;
void Check(string name, Action test)
{
    test();
    Console.WriteLine("PASS " + name);
    passed++;
}
void Require(bool value, string message)
{
    if (!value) throw new Exception(message);
}

Check("credential options are hidden", () =>
{
    foreach (var cmd in new[] { "username", "password", "password_enc" })
    {
        Require(ArgTrace.IsSecret(cmd), cmd + " is not secret");
        Require(ArgTrace.Value(cmd, "s3cret-value") == ArgTrace.Hidden, cmd + " leaks its value");
        Require(ArgTrace.Value(cmd, "") == ArgTrace.Hidden, cmd + " shows an empty value");
    }
});

Check("other options keep their value", () =>
{
    foreach (var cmd in new[] { "ip", "port", "autologin", "clientversion", "lastcharactername", "settings" })
    {
        Require(!ArgTrace.IsSecret(cmd), cmd + " is secret");
        Require(ArgTrace.Value(cmd, "v1") == "v1", cmd + " lost its value");
    }
});

// Main.cs is ported upstream code that needs the whole client to build, so
// the arg loop itself is checked from its source.
string root = AppContext.BaseDirectory;
while (root != null && !File.Exists(Path.Combine(root, "godot", "GUO", "GUO.csproj")))
    root = Path.GetDirectoryName(root);
Require(root != null, "repository root not found");
string main = File.ReadAllText(Path.Combine(root, "godot", "GUO", "src", "Client", "Main.cs"));

Check("Main.cs traces every arg value through ArgTrace", () =>
{
    var traces = Regex.Matches(main, @"Log\.\w+\(\$""ARG:[^\n]*");
    Require(traces.Count == 1, $"expected one ARG trace, found {traces.Count}");
    Require(traces[0].Value.Contains("ArgTrace.Value(cmd, value)"), "ARG trace logs the raw value: " + traces[0].Value);
    Require(!Regex.IsMatch(main, @"Log\.\w+\([^\n]*\{value\}"), "a log line still prints {value}");
});

Check("every credential option in the arg loop is in the secret list", () =>
{
    foreach (Match m in Regex.Matches(main, @"case ""([a-z_]+)"":"))
    {
        string cmd = m.Groups[1].Value;
        if (cmd.Contains("pass") || cmd.Contains("user") || cmd.Contains("token") || cmd.Contains("secret"))
            Require(ArgTrace.IsSecret(cmd), "credential option '" + cmd + "' is not hidden");
    }
});

// PERF-V6: --perf-static-world thins the world the render lists see. It is a
// probe-only flag: refused without --perf-probe, and the live switch is set
// only by PerfProbe.Run for the probe's lifetime, never by the arg loop.
string bootstrap = File.ReadAllText(Path.Combine(root, "godot", "GUO", "src", "Bootstrap", "Main.cs"));
string probe = File.ReadAllText(Path.Combine(root, "godot", "GUO", "src", "Bootstrap", "PerfProbe.cs"));

Check("--perf-static-world is refused without --perf-probe", () =>
{
    Require(GUO.Host.PerfProbeFlags.ProbeOnlyError(perfProbe: false, staticWorld: true) != null, "accepted without the probe");
    Require(GUO.Host.PerfProbeFlags.ProbeOnlyError(perfProbe: true, staticWorld: true) == null, "refused with the probe");
    Require(GUO.Host.PerfProbeFlags.ProbeOnlyError(perfProbe: false, staticWorld: false) == null, "refused when absent");
    Require(GUO.Host.PerfProbeFlags.ProbeOnlyError(perfProbe: true, staticWorld: false) == null, "refused a plain probe");
    Require(Regex.IsMatch(bootstrap, @"PerfProbeFlags\.ProbeOnlyError\(_options\.PerfProbe, _options\.PerfStaticWorld\)"), "Main.cs does not check the combination after parsing");
});

Check("the arg loop never switches the static world on itself", () =>
{
    var arm = Regex.Match(bootstrap, @"case ""--perf-static-world"":\s*([^\n]*)");
    Require(arm.Success, "the --perf-static-world case is gone");
    Require(arm.Groups[1].Value.Trim() == "o.PerfStaticWorld = true;", "the case does more than record the option: " + arm.Groups[1].Value.Trim());
    Require(!bootstrap.Contains("PerfProbe.StaticWorld ="), "Main.cs assigns PerfProbe.StaticWorld");
    Require(Regex.IsMatch(probe, @"public static bool StaticWorld \{ get; private set; \}"), "StaticWorld is settable from outside PerfProbe");
    var sets = Regex.Matches(probe, @"^\s*StaticWorld = (\w+);", RegexOptions.Multiline);
    Require(sets.Count == 2 && sets[0].Groups[1].Value == "staticWorld" && sets[1].Groups[1].Value == "false",
        "PerfProbe must set StaticWorld once from Run's argument and clear it once, found " + sets.Count);
    Require(Regex.IsMatch(probe, @"finally\s*\{\s*StaticWorld = false;"), "StaticWorld is not cleared in a finally");
});

Console.WriteLine($"{passed} checks passed");
