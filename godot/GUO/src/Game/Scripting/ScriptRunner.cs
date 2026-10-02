// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace GUO.Game.Scripting;

/// <summary>GUO's independently implemented, limited Razor CE command runner.
/// Single-threaded: Start, Stop and Tick belong to the game thread. No Razor code is linked.</summary>
internal sealed partial class ScriptRunner
{
    public const int MaximumLength = 65536;
    private readonly IScriptHost _host;
    private readonly List<Instruction> _program = new();
    private int _position;
    private long? _deadline;
    private long _nextAction;
    public bool Running { get; private set; }
    public int Line { get; private set; }
    public string Status { get; private set; } = "Ready";

    private sealed record Instruction(int Line, string Command, int Delay, Action Action)
    {
        public ScriptWords Args;
        public Func<int> Duration;
        public int Jump, End, Group;
    }

    public ScriptRunner(IScriptHost host) => _host = host;

    // Validate the entire script before allowing any game action. Unknown syntax is an error.
    public bool Start(string source)
    {
        Stop();
        _program.Clear();
        ResetFlow();
        Line = 0;
        try
        {
            if (!_host.Connected) throw new FormatException("Log in before running a script.");
            if (source.Length > MaximumLength) throw new FormatException("Script exceeds 64K characters.");
            string[] lines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                Line = i + 1;
                ScriptWords tokens = Tokenize(lines[i]);
                ScriptWords words = tokens.WordRange(1, Math.Max(0, tokens.Count - 1));
                if (tokens.Count == 0) continue;
                string command = tokens[0].ToLowerInvariant();
                if (CompileFlow(command, words)) continue;
                int delay = 0;
                Func<int> duration = null;
                Action action = null;
                switch (command)
                {
                    case "pause":
                    case "wait":
                        Require(words, 1);
                        duration = CompileDuration(words[0]);
                        command = "pause";
                        break;
                    case "waitfortarget":
                    case "wft":
                        _host.ValidateTargetWait();
                        if (words.Count > 1) throw new FormatException("Expected an optional timeout in milliseconds.");
                        duration = words.Count == 0 ? () => 30000 : CompileDuration(words[0]);
                        command = "waitfortarget";
                        break;
                    case "stop":
                        Require(words, 0);
                        break;
                    default:
                        _host.Validate(command, words);
                        if (!words.Exists(word => _declaredVariables.Contains(word) || word == "index"))
                            _host.Bind(command, words); // Validate literal values without executing the prepared action.
                        action = () => _host.Bind(command, words.ConvertAll(Resolve))();
                        break;
                }
                _program.Add(new Instruction(Line, command, delay, action) { Duration = duration });
            }
            FinishFlow();
            if (_program.Count == 0) throw new FormatException("Script is empty.");
            _position = 0;
            _nextAction = 0;
            Line = _program[0].Line;
            Running = true;
            Status = "Running";
            return true;
        }
        catch (FormatException ex)
        {
            Status = $"Line {Line}: {ex.Message}";
            _program.Clear();
            return false;
        }
    }

    public void Stop(string reason = "Stopped")
    {
        Running = false;
        _deadline = null;
        Status = reason;
    }

    /// <summary>Monotonic milliseconds, supplied by the host; one instruction per tick,
    /// at least 25 ms between actions. Waiting never sleeps the game thread.</summary>
    public void Tick(long now)
    {
        if (!Running) return;
        if (!_host.Connected) { Stop("Disconnected"); return; }
        if (now < _nextAction) return;
        Instruction instruction = _program[_position];
        Line = instruction.Line;
        try
        {
            if (ExecuteFlow(instruction, now)) { }
            else if (instruction.Command == "pause" || instruction.Command == "waitfortarget")
            {
                _deadline ??= now + instruction.Duration();
                if (instruction.Command == "waitfortarget")
                {
                    if (!_host.HasTarget)
                    {
                        if (now >= _deadline) Stop($"Line {Line}: Target wait timed out.");
                        return;
                    }
                }
                else if (now < _deadline) return;
                _deadline = null;
            }
            else if (instruction.Command == "stop") { Stop(); return; }
            else instruction.Action();

            if (++_position == _program.Count) Stop("Completed");
            else _nextAction = now + 25;
        }
        catch (Exception ex)
        {
            Stop($"Line {Line}: {ex.Message}");
        }
    }

    public static void Require(IReadOnlyList<string> args, int count)
    {
        if (args.Count != count) throw new FormatException($"Expected {count} argument(s); quote text containing spaces.");
    }

    private static int Milliseconds(string value)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int ms) || ms > 3600000)
            throw new FormatException("Timeout must be between 0 and 3600000 milliseconds.");
        return ms;
    }

    private Func<int> CompileDuration(string token)
    {
        // Resolve variables when the wait starts, rather than while compiling.
        if (!_declaredVariables.Contains(token))
        {
            int constant = Milliseconds(token);
            return () => constant;
        }
        return () => Milliseconds(Resolve(token));
    }

    // Keep quote information through expression slices: quoted operators are data.
    private sealed class ScriptWords : List<string>
    {
        public readonly HashSet<int> Quoted = new();
        public ScriptWords WordRange(int start, int count)
        {
            var result = new ScriptWords();
            for (int i = 0; i < count; i++)
            {
                result.Add(this[start + i]);
                if (Quoted.Contains(start + i)) result.Quoted.Add(i);
            }
            return result;
        }
    }

    private static ScriptWords Tokenize(string line)
    {
        var words = new ScriptWords();
        int i = 0;
        while (i < line.Length)
        {
            if (char.IsWhiteSpace(line[i])) { i++; continue; }
            if (line[i] == '#' || (line[i] == '/' && i + 1 < line.Length && line[i + 1] == '/')) break;
            var word = new StringBuilder();
            char quote = line[i] == '\'' || line[i] == '"' ? line[i++] : '\0';
            if (quote != '\0')
            {
                while (i < line.Length && line[i] != quote) word.Append(line[i++]);
                if (i == line.Length) throw new FormatException("Unclosed quoted text.");
                i++;
                if (i < line.Length && !char.IsWhiteSpace(line[i])) throw new FormatException("Expected a space after quoted text.");
            }
            else
            {
                while (i < line.Length && !char.IsWhiteSpace(line[i])) word.Append(line[i++]);
            }
            if (quote != '\0') words.Quoted.Add(words.Count);
            words.Add(word.ToString());
        }
        return words;
    }
}

internal interface IScriptHost
{
    bool Connected { get; }
    bool HasTarget { get; }
    void ValidateTargetWait() { }
    // Bind must only validate and prepare an action, never execute it.
    Action Bind(string command, IReadOnlyList<string> args);
    void Validate(string command, IReadOnlyList<string> args) => Bind(command, args);
    bool HasExpression(string name) => false;
    void ValidateExpression(string name, IReadOnlyList<string> args) { }
    object Evaluate(string name, IReadOnlyList<string> args) => throw new FormatException($"Unsupported expression '{name}'.");
}
