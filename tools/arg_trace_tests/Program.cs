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

Console.WriteLine($"{passed} checks passed");
