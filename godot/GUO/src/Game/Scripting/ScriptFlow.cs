// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace GUO.Game.Scripting;

// Original GUO implementation of documented CE control flow. No upstream implementation is included.
internal sealed partial class ScriptRunner
{
    private sealed class Block
    {
        public int Start;
        public string Kind;
        public List<int> Branches = new();
        public List<int> Breaks = new();
        public List<int> Continues = new();
        public bool Else;
    }
    private readonly Stack<Block> _blocks = new();
    private readonly Dictionary<string, string> _variables = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _lists = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _timers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, int> _iterations = new();
    private readonly HashSet<int> _taken = new();
    private readonly HashSet<string> _declaredVariables = new(StringComparer.OrdinalIgnoreCase);
    private long _now;

    private void ResetFlow()
    {
        _blocks.Clear(); _variables.Clear(); _lists.Clear(); _timers.Clear();
        _iterations.Clear(); _taken.Clear();
        _declaredVariables.Clear();
    }

    private bool CompileFlow(string command, ScriptWords args)
    {
        int position = _program.Count;
        var instruction = new Instruction(Line, command, 0, null) { Args = args };
        switch (command)
        {
            case "if": case "while": case "for": case "foreach":
                if (_blocks.Count >= 64) throw new FormatException("Blocks may nest at most 64 levels.");
                if (command == "for") Require(args, 1);
                else if (command == "foreach")
                {
                    Require(args, 3);
                    if (args[1] != "in") throw new FormatException("Expected foreach 'variable' in 'list'.");
                    _declaredVariables.Add(args[0]);
                }
                else Expression(args, true);
                _blocks.Push(new Block { Start = position, Kind = command, Branches = new List<int> { position } });
                instruction.Group = position;
                break;
            case "elseif": case "else":
                if (!_blocks.TryPeek(out var branch) || branch.Kind != "if" || branch.Else)
                    throw new FormatException("Unexpected " + command + ".");
                if (command == "else") { Require(args, 0); branch.Else = true; }
                else Expression(args, true);
                _program[branch.Branches[^1]].Jump = position;
                instruction.Group = branch.Start;
                branch.Branches.Add(position);
                break;
            case "endif": case "endwhile": case "endfor":
                Require(args, 0);
                if (!_blocks.TryPop(out var block) || (command == "endif" ? block.Kind != "if" :
                    command == "endwhile" ? block.Kind != "while" : block.Kind != "for" && block.Kind != "foreach"))
                    throw new FormatException("Mismatched " + command + ".");
                instruction.Group = block.Start;
                instruction.Jump = block.Start;
                foreach (int index in block.Branches) _program[index].End = position;
                _program[block.Branches[^1]].Jump = position;
                foreach (int index in block.Breaks) { _program[index].Jump = position + 1; _program[index].End = position; }
                foreach (int index in block.Continues) { _program[index].Jump = position; _program[index].End = position; }
                break;
            case "break": case "continue":
                Require(args, 0);
                var loop = _blocks.FirstOrDefault(b => b.Kind != "if");
                if (loop == null) throw new FormatException(command + " requires a loop.");
                instruction.Group = loop.Start;
                (command == "break" ? loop.Breaks : loop.Continues).Add(position);
                break;
            case "loop": case "replay": Require(args, 0); break;
            case "setvar": case "setvariable":
                Require(args, 2); _declaredVariables.Add(args[0]); break;
            case "settimer": case "pushlist": case "poplist":
                Require(args, 2); break;
            case "unsetvar": case "unsetvariable": case "createlist": case "clearlist": case "removelist": case "createtimer": case "removetimer":
                Require(args, 1); break;
            default: return false;
        }
        _program.Add(instruction);
        return true;
    }

    private void FinishFlow()
    {
        if (_blocks.TryPeek(out var block))
        {
            Line = _program[block.Start].Line;
            throw new FormatException("Unclosed " + block.Kind + " block.");
        }
    }

    private string Resolve(string token) => token == "index" && _iterations.Count > 0 ?
        _iterations.MaxBy(pair => pair.Key).Value.ToString(CultureInfo.InvariantCulture) :
        _variables.TryGetValue(token, out string value) ? value : token;
    private static bool Truth(object value) => value is bool b ? b :
        double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out double n) ? n != 0 :
        bool.TryParse(Convert.ToString(value), out bool flag) ? flag : !string.IsNullOrEmpty(Convert.ToString(value));
    private static double Number(object value)
    {
        string text = Convert.ToString(value, CultureInfo.InvariantCulture);
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && uint.TryParse(text.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hex)) return hex;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double result) && double.IsFinite(result)) return result;
        throw new FormatException("Expected a number, got '" + text + "'.");
    }

    private bool ExecuteFlow(Instruction instruction, long now)
    {
        if (instruction.Args == null) return false;
        _now = now;
        ScriptWords args = instruction.Args;
        string name = args.Count > 0 ? args[0] : "";
        switch (instruction.Command)
        {
            case "if":
                _taken.Remove(instruction.Group);
                goto case "elseif";
            case "elseif": case "else":
                if (_taken.Contains(instruction.Group)) { _taken.Remove(instruction.Group); _position = instruction.End; }
                else if (instruction.Command == "else" || Truth(Expression(args))) _taken.Add(instruction.Group);
                else _position = instruction.Jump - 1;
                break;
            case "endif": _taken.Remove(instruction.Group); break;
            case "while": case "for": case "foreach":
                int iteration = _iterations.GetValueOrDefault(_position);
                _iterations[_position] = iteration;
                _variables["index"] = iteration.ToString(CultureInfo.InvariantCulture);
                bool run = instruction.Command == "while" ? Truth(Expression(args)) : instruction.Command == "for" ?
                    iteration < Number(Resolve(name)) : iteration < GetList(args[2]).Count;
                if (!run) { _iterations.Remove(_position); _position = instruction.End; }
                else if (instruction.Command == "foreach") _variables[name] = GetList(args[2])[iteration];
                break;
            case "endwhile": case "endfor":
                _iterations[instruction.Group] = checked(_iterations.GetValueOrDefault(instruction.Group) + 1);
                _position = instruction.Jump - 1;
                break;
            case "break": case "continue":
                _taken.RemoveWhere(index => index > instruction.Group && index < instruction.End);
                if (instruction.Command == "break") _iterations.Remove(instruction.Group);
                _position = instruction.Jump - 1;
                break;
            case "loop": case "replay":
                _iterations.Clear(); _taken.Clear(); _position = -1; break;
            case "setvar": case "setvariable":
                _variables[name] = Resolve(args[1]); break;
            case "unsetvar": case "unsetvariable": _variables.Remove(name); break;
            case "createlist": _lists.TryAdd(name, new List<string>()); break;
            case "clearlist": GetList(name).Clear(); break;
            case "removelist": _lists.Remove(name); break;
            case "pushlist":
                var list = GetList(name);
                if (list.Count >= 65536) throw new InvalidOperationException("List exceeds 65536 entries.");
                list.Add(Resolve(args[1])); break;
            case "poplist": Pop(name, Resolve(args[1])); break;
            case "createtimer": _timers.TryAdd(name, now); break;
            case "settimer": _timers[name] = now - checked((long)Number(Resolve(args[1]))); break;
            case "removetimer": _timers.Remove(name); break;
        }
        return true;
    }

    private List<string> GetList(string name) => _lists.TryGetValue(name, out var list) ? list : throw new InvalidOperationException("Unknown list '" + name + "'.");
    private bool Pop(string name, string item)
    {
        var list = GetList(name);
        if (list.Count == 0) return false;
        if (item == "front") { list.RemoveAt(0); return true; }
        if (item == "back") { list.RemoveAt(list.Count - 1); return true; }
        return list.Remove(item);
    }

    // Each expression is validated at compile time, including branches that may never run.
    private object Expression(ScriptWords args, bool validate = false, bool literalOperand = false)
    {
        if (args.Count == 0) throw new FormatException("Expected an expression.");
        foreach (string logical in new[] { "or", "and" })
        {
            int split = -1;
            for (int i = 0; i < args.Count; i++)
                if (!args.Quoted.Contains(i) && args[i] == logical) { split = i; break; }
            if (split < 0) continue;
            object left = Expression(args.WordRange(0, split), validate);
            var rightArgs = args.WordRange(split + 1, args.Count - split - 1);
            if (validate) { Expression(rightArgs, true); return false; }
            return logical == "or" ? Truth(left) || Truth(Expression(rightArgs)) : Truth(left) && Truth(Expression(rightArgs));
        }
        if (!args.Quoted.Contains(0) && args[0] == "not") return !Truth(Expression(args.WordRange(1, args.Count - 1), validate));
        for (int i = 1; i < args.Count; i++)
        {
            string op = args[i];
            if (args.Quoted.Contains(i)) continue;
            if (op is not ("=" or "==" or "!=" or "<" or ">" or "<=" or ">=" or "in")) continue;
            object left = Expression(args.WordRange(0, i), validate, true), right = Expression(args.WordRange(i + 1, args.Count - i - 1), validate, true);
            if (validate) return false;
            if (op == "in") return Convert.ToString(right).Contains(Convert.ToString(left), StringComparison.OrdinalIgnoreCase);
            int comparison;
            try { comparison = Number(left).CompareTo(Number(right)); }
            catch (FormatException) { comparison = string.Compare(Convert.ToString(left), Convert.ToString(right), StringComparison.OrdinalIgnoreCase); }
            return op switch { "=" or "==" => comparison == 0, "!=" => comparison != 0, "<" => comparison < 0, ">" => comparison > 0, "<=" => comparison <= 0, _ => comparison >= 0 };
        }
        if (args.Count == 1 && args.Quoted.Contains(0)) return validate ? 0 : Resolve(args[0]);
        string expression = args[0].ToLowerInvariant();
        var parameters = args.Skip(1).ToList();
        if (expression is "varexist" or "varexists" or "listexists" or "list" or "timerexists" or "timer")
        {
            Require(parameters, 1);
            if (validate) return 0;
            string name = parameters[0];
            return expression switch
            {
                "varexist" or "varexists" => _variables.ContainsKey(name), "listexists" => _lists.ContainsKey(name),
                "list" => GetList(name).Count, "timerexists" => _timers.ContainsKey(name),
                _ => _timers.TryGetValue(name, out long start) ? _now - start : throw new InvalidOperationException("Unknown timer '" + name + "'.")
            };
        }
        if (expression is "inlist" or "poplist")
        {
            Require(parameters, 2);
            if (validate) return false;
            return expression == "inlist" ? GetList(parameters[0]).Contains(Resolve(parameters[1])) : Pop(parameters[0], Resolve(parameters[1]));
        }
        if (_host.HasExpression(expression))
        {
            _host.ValidateExpression(expression, parameters);
            return validate ? 0 : _host.Evaluate(expression, parameters.ConvertAll(Resolve));
        }
        if (parameters.Count != 0) throw new FormatException("Unsupported expression '" + expression + "'.");
        if (!literalOperand && !_declaredVariables.Contains(args[0]) && args[0] != "index" &&
            !bool.TryParse(args[0], out _) && !double.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            throw new FormatException("Unknown condition '" + args[0] + "'.");
        return validate ? 0 : Resolve(args[0]);
    }
}
