// SPDX-License-Identifier: BSD-2-Clause
using GUO.Game.Scripting;

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
    passed++;
}

var host = new FakeHost();
var runner = new ScriptRunner(host);
Check(!runner.Start("emit first\nunknown second") && host.Messages.Count == 0 && runner.Status.StartsWith("Line 2:"), "whole-script validation precedes actions");
Check(!runner.Start("emit 'unclosed") && !runner.Running, "unclosed strings rejected");
Check(!runner.Start("emit 'first'garbage"), "adjacent quoted tokens rejected");
Check(!runner.Start("pause -1") && !runner.Start("pause 3600001"), "invalid delays rejected");
Check(!runner.Start(new string('a', ScriptRunner.MaximumLength + 1)), "source size bounded");
Check(!runner.Start("// comments only\n# nothing"), "empty scripts rejected");
Check(runner.Start("# comment\n\nemit 'hello // # world' // trailing\nemit \"second message\""), "quotes and comments accepted");
runner.Tick(0);
runner.Tick(0);
Check(host.Messages.SequenceEqual(new[] { "hello // # world" }) && runner.Line == 3, "one action per tick with source line preserved");
runner.Tick(24);
Check(host.Messages.Count == 1, "action pacing enforced");
runner.Tick(25);
Check(host.Messages[^1] == "second message" && runner.Status == "Completed", "script completes");

host.Messages.Clear();
runner.Start("pause 100\nemit done");
runner.Tick(1000);
runner.Tick(1099);
Check(runner.Running && host.Messages.Count == 0, "pause yields without executing following command");
runner.Tick(1100);
runner.Tick(1125);
Check(host.Messages.SequenceEqual(new[] { "done" }), "pause resumes at deadline");

host.Messages.Clear();
runner.Start("wft 100\nemit target");
runner.Tick(2000);
runner.Tick(2099);
host.HasTarget = true;
runner.Tick(2100);
runner.Tick(2125);
Check(host.Messages.SequenceEqual(new[] { "target" }), "target at deadline wins over timeout");
host.HasTarget = false;
runner.Start("waitfortarget 100\nemit forbidden");
runner.Tick(3000);
runner.Tick(3100);
Check(!runner.Running && runner.Status.Contains("timed out") && host.Messages.Count == 1, "target timeout stops following actions");

runner.Start("pause 100\nemit forbidden");
runner.Tick(4000);
runner.Stop();
runner.Tick(5000);
Check(host.Messages.Count == 1 && runner.Status == "Stopped", "stop cancels waiting work");
runner.Start("emit forbidden");
host.Connected = false;
runner.Tick(6000);
Check(!runner.Running && runner.Status == "Disconnected" && host.Messages.Count == 1, "disconnect cancels before next action");
Check(!runner.Start("emit forbidden"), "cannot start disconnected");
host.Connected = true;
runner.Start("fail\nemit forbidden");
runner.Tick(7000);
Check(!runner.Running && runner.Status == "Line 1: Action failed" && host.Messages.Count == 1, "action failure terminates with line number");
runner.Start("stop\nemit forbidden");
runner.Tick(8000);
Check(!runner.Running && host.Messages.Count == 1, "explicit stop terminates program");
Check(runner.Start("emit restarted"), "restart after stop");
runner.Tick(9000);
Check(host.Messages[^1] == "restarted", "restart has no stale wait state");

void RunScript(string source, int budget = 500)
{
    host.Messages.Clear();
    if (!runner.Start(source)) throw new Exception(runner.Status);
    for (int i = 0; i < budget && runner.Running; i++) runner.Tick(i * 25);
}
RunScript("if 1 = 2\nemit no\nelseif 2 = 2\nemit yes\nelse\nemit no\nendif");
Check(host.Messages.SequenceEqual(new[] { "yes" }) && runner.Status == "Completed", "elseif selects exactly one branch");
RunScript("for 3\nemit index\nendfor");
Check(host.Messages.SequenceEqual(new[] { "0", "1", "2" }), "counted loop exposes zero-based index");
RunScript("for 2\nfor 1\nemit inner\nendfor\nemit index\nendfor");
Check(host.Messages.SequenceEqual(new[] { "inner", "0", "inner", "1" }), "nested loops restore outer index");
RunScript("for 5\nif index = 1\ncontinue\nendif\nif index = 3\nbreak\nendif\nemit index\nendfor");
Check(host.Messages.SequenceEqual(new[] { "0", "2" }), "break and continue unwind nested conditions");
RunScript("setvar done false\nwhile not done\nemit once\nsetvar done true\nendwhile");
Check(host.Messages.SequenceEqual(new[] { "once" }), "while condition reevaluates variables");
RunScript("createlist names\npushlist names first\npushlist names second\nforeach name in names\nemit name\nendfor");
Check(host.Messages.SequenceEqual(new[] { "first", "second" }), "foreach iterates list values");
RunScript("createtimer reminder\nwhile timer reminder < 100\npause 0\nendwhile\nemit done");
Check(host.Messages.SequenceEqual(new[] { "done" }) && !runner.Running, "timer expression uses injected monotonic clock");
RunScript("createlist things\npushlist things one\nif listexists things and inlist things one\nemit yes\nendif\npoplist things front\nif list things = 0\nemit empty\nendif");
Check(host.Messages.SequenceEqual(new[] { "yes", "empty" }), "list expressions and removal");
Check(!runner.Start("emit forbidden\nfor 3\nendif") && runner.Status.StartsWith("Line 3:"), "mismatched blocks rejected before execution");
Check(!runner.Start("if true\nemit forbidden") && runner.Status.StartsWith("Line 1:"), "unclosed blocks report opening line");
RunScript("while true\nendwhile", 20);
Check(runner.Running, "empty infinite loops yield instead of blocking");
runner.Stop();
Check(!runner.Running, "empty infinite loop can be stopped");
RunScript("emit again\nreplay", 6);
Check(runner.Running && host.Messages.Count == 3, "replay restarts without recursion");
runner.Stop();

RunScript("setvar duration 50\npause duration\nemit finished");
Check(host.Messages.SequenceEqual(new[] { "finished" }) && runner.Status == "Completed", "wait resolves a variable at execution");
RunScript("setvar duration 0\nsetvar duration 100\npause duration\nemit forbidden", 5);
Check(host.Messages.Count == 0 && runner.Running, "wait uses the current variable value");
runner.Stop();
RunScript("setvar duration invalid\npause duration\nemit forbidden");
Check(host.Messages.Count == 0 && runner.Status.StartsWith("Line 2:"), "invalid dynamic wait stops before subsequent actions");
Check(!runner.Start("emit forbidden\nif true or hp unexpected\nemit no\nendif"), "host expression arguments validated even in short-circuited branches");
RunScript("if hp > 10\nemit healthy\nendif");
Check(host.Messages.SequenceEqual(new[] { "healthy" }), "host expression evaluated at execution");
Check(!runner.Start("emit forbidden\nstrict invalid"), "literal command values validated before any action");
RunScript("setvar value valid\nstrict value");
Check(host.Messages.SequenceEqual(new[] { "valid" }), "dynamic command values validated after variable resolution");

var completion = ScriptCatalog.Complete("  ca", 4, new[] { "heal", "greater heal" });
Check(!runner.Start("emit forbidden\nif misspelledcondition\nemit no\nendif"), "unknown conditions do not silently become true");
Check(!runner.Start(string.Concat(Enumerable.Repeat("if true\n", 65)) + string.Concat(Enumerable.Repeat("endif\n", 65))), "nesting limit fails predictably");
RunScript("for 0\nemit forbidden\nendfor\nemit after");
Check(host.Messages.SequenceEqual(new[] { "after" }), "zero-count loop skips its body");
RunScript("createlist values\npushlist values one\ncreatelist values\nif true or poplist values front\nemit yes\nendif\nif false and poplist values front\nemit forbidden\nendif\nif list values = 1\nemit retained\nendif");
Check(host.Messages.SequenceEqual(new[] { "yes", "retained" }), "boolean short circuit avoids list side effects and create preserves entries");
RunScript("settimer elapsed 1000\nif timer elapsed >= 1000\nemit elapsed\nendif\nremovetimer elapsed\nif not timerexists elapsed\nemit removed\nendif");
Check(host.Messages.SequenceEqual(new[] { "elapsed", "removed" }), "timer initialization and removal");
RunScript("setvar flag true\nunsetvariable flag\nif not varexists flag\nemit removed\nendif");
Check(host.Messages.SequenceEqual(new[] { "removed" }), "variable alias and existence after removal");
RunScript("pushlist missing value\nemit forbidden");
Check(host.Messages.Count == 0 && runner.Status.Contains("Unknown list"), "missing lists stop with useful runtime errors");
RunScript("if 'and' = 'and' and 'or' != 'not'\nemit quoted\nendif");
Check(host.Messages.SequenceEqual(new[] { "quoted" }), "quoted logical operators remain string data");
RunScript("if 'in' in 'within'\nemit substring\nendif");
Check(host.Messages.SequenceEqual(new[] { "substring" }), "quoted comparison operator remains string data");
RunScript("if 'hp' = 'hp'\nemit literal\nendif");
Check(host.Messages.SequenceEqual(new[] { "literal" }), "quoted expression names remain values");
Check(completion.Start == 2 && completion.Candidates.SequenceEqual(new[] { "cast" }), "command completion respects indentation");
completion = ScriptCatalog.Complete("cast 'gre", 9, new[] { "heal", "greater heal" });
Check(completion.Start == 6 && completion.Candidates.SequenceEqual(new[] { "greater heal" }), "spell completion understands quoted arguments");
Check(ScriptCatalog.Complete("// ca", 5, Array.Empty<string>()).Candidates.Length == 0, "no completion inside comments");

// Filesystem integration: an isolated temporary profile, always removed.
string directory = Path.Combine(Path.GetTempPath(), "guo-script-test-" + Guid.NewGuid().ToString("N"));
try
{
    var library = new ScriptLibrary(directory);
    Check(library.Names().Length == 0, "new profile has empty library");
    library.Save("example", "emit 'saved'");
    library.Save("CON", "emit 'portable name'");
    Check(library.Read("example") == "emit 'saved'" && library.Names().Length == 2, "library round trip and portable names");
    library.Save("example", "emit 'updated'");
    Check(library.Read("example") == "emit 'updated'" && !Directory.GetFiles(directory, "*.tmp", SearchOption.AllDirectories).Any(), "save replaces atomically without temporary remnants");
    string firstCopy = library.AddStarter(ScriptCatalog.Starters[0]);
    library.Save(firstCopy, "personal edits");
    string secondCopy = library.AddStarter(ScriptCatalog.Starters[0]);
    Check(firstCopy != secondCopy && library.Read(firstCopy) == "personal edits" && library.Read(secondCopy) == ScriptCatalog.Starters[0].Source, "starter copies never overwrite personal edits");
    string longName = new string('x', 64);
    string longCopy = library.AddCopy(longName, "emit original", "Pack attribution");
    string otherLongCopy = library.AddCopy(longName, "emit second", "Second attribution");
    Check(longCopy.Length == 64 && otherLongCopy.Length == 64 && otherLongCopy.EndsWith("-2") && library.Read(longCopy) == "emit original", "long pack names remain bounded when duplicated");
    Check(File.ReadAllText(Path.Combine(directory, "scripts", "script-" + longCopy + ".razor.LICENSE.txt")) == "Pack attribution", "personal copy retains separate attribution");
    foreach (string bad in new[] { "../escape", "a/b", "a\\b", "a:b", "", "..", "name.razor" })
    {
        bool rejected = false;
        try { library.Save(bad, "emit forbidden"); } catch (IOException) { rejected = true; }
        Check(rejected, "reject unsafe library name: " + bad);
    }
}
finally
{
    if (Directory.Exists(directory)) Directory.Delete(directory, true);
}
var capabilityInner = new CapabilityProbeHost();
bool lease = true;
var grants = new List<string> { "client.message" };
var restricted = new CapabilityScriptHost(capabilityInner, grants, () => lease);
var managedRunner = new ScriptRunner(restricted);
Check(!managedRunner.Start("sysmsg before\nsay forbidden") && capabilityInner.Calls == 0,
    "missing action capability rejects whole script before side effects");
Check(!managedRunner.Start("sysmsg before\nwft 100"), "target wait requires declared capability before execution");
Check(!managedRunner.Start("if hp > 0\nsysmsg forbidden\nendif"), "world expressions require read capability");
Check(!managedRunner.Start("if insysmsg secret\nsysmsg forbidden\nendif"), "journal expressions require separate read capability");
grants.Add("player.speech");
Check(!managedRunner.Start("say forbidden"), "mutating caller capability list cannot expand grants");
Check(managedRunner.Start("sysmsg allowed"), "declared capability permits compilation without execution");
Check(capabilityInner.Calls == 0, "capability validation does not execute prepared actions");
managedRunner.Tick(0);
Check(capabilityInner.Calls == 1, "approved action executes through host");
Action prepared = restricted.Bind("sysmsg", new[] { "prepared" });
lease = false;
bool revoked = false;
try { prepared(); } catch (FormatException) { revoked = true; }
Check(revoked && capabilityInner.Calls == 1, "revocation blocks previously prepared action");
lease = true;
Check(managedRunner.Start("pause 100\nsysmsg forbidden"), "approved waiting script starts");
managedRunner.Tick(0);
lease = false;
managedRunner.Tick(100);
Check(!managedRunner.Running && capabilityInner.Calls == 1, "revocation stops pending execution");
bool unknownCapability = false;
try { _ = new CapabilityScriptHost(capabilityInner, new[] { "future.execute" }, () => true); }
catch (FormatException) { unknownCapability = true; }
Check(unknownCapability, "unknown capabilities fail closed");
var fullyGranted = new CapabilityScriptHost(capabilityInner,
    new[] { "world.read", "journal.read", "player.target" }, () => true);
Check((int)fullyGranted.Evaluate("hp", Array.Empty<string>()) == 50 &&
    (bool)fullyGranted.Evaluate("insysmsg", new[] { "text" }) && fullyGranted.HasTarget,
    "declared reads and targeting reach host");
bool unmapped = false;
try { fullyGranted.Bind("futurecommand", Array.Empty<string>()); } catch (FormatException) { unmapped = true; }
Check(unmapped, "new commands require explicit capability mapping");
ScriptPackChecks.Run(Check);
Console.WriteLine($"{passed} checks passed.");

sealed class CapabilityProbeHost : IScriptHost
{
    public int Calls;
    public bool Connected => true;
    public bool HasTarget => true;
    public bool HasExpression(string name) => name is "hp" or "insysmsg";
    public object Evaluate(string name, IReadOnlyList<string> args) => name == "hp" ? 50 : true;
    public Action Bind(string command, IReadOnlyList<string> args) => () => Calls++;
}

sealed class FakeHost : IScriptHost
{
    public bool Connected { get; set; } = true;
    public bool HasTarget { get; set; }
    public List<string> Messages { get; } = new();
    public bool HasExpression(string name) => name == "hp";
    public void ValidateExpression(string name, IReadOnlyList<string> args) => ScriptRunner.Require(args, 0);
    public object Evaluate(string name, IReadOnlyList<string> args) => 50;
    public void Validate(string command, IReadOnlyList<string> args)
    {
        if (command == "strict") ScriptRunner.Require(args, 1);
        else Bind(command, args);
    }
    public Action Bind(string command, IReadOnlyList<string> args)
    {
        if (command == "fail") return () => throw new InvalidOperationException("Action failed");
        if (command == "strict")
        {
            if (args.Count != 1 || args[0] != "valid") throw new FormatException("Expected valid");
            return () => Messages.Add(args[0]);
        }
        if (command != "emit") throw new FormatException("Unknown command");
        ScriptRunner.Require(args, 1);
        return () => Messages.Add(args[0]);
    }
}
