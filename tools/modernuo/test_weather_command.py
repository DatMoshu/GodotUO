"""WX-02 MUO patch: compile the actual GM command with packet/registration stand-ins.

No shard, account or socket is touched. Checks declared GM registration and exact
caller-only packet requests; deployed command discovery is a separate live gate.
"""
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

HERE = Path(__file__).resolve().parent

HARNESS = r'''
using System;
using System.Collections.Generic;
using Server;
using Server.Commands;
using Server.Custom.Guo;

public static class Program
{
    static int checks;
    static void Check(bool ok, string why) { checks++; if (!ok) throw new Exception(why); }
    public static void Main()
    {
        GuoWeather.Configure();
        Check(CommandSystem.Name == "GuoWeather" && CommandSystem.Level == AccessLevel.GameMaster,
            "command must declare GM access");
        var gm = new Mobile();
        var other = new Mobile();
        foreach (var mode in new[] { ("rain", 0), ("storm", 1), ("snow", 2), ("brewing", 3) })
        {
            CommandSystem.Run(AccessLevel.GameMaster, gm, mode.Item1, "35");
            Check(gm.NetState.Packets[^1] == ((byte)mode.Item2, (byte)35, (byte)15), "mode/count bytes");
        }
        CommandSystem.Run(AccessLevel.GameMaster, gm, "rain");
        Check(gm.NetState.Packets[^1] == ((byte)0, (byte)70, (byte)15), "default count");
        CommandSystem.Run(AccessLevel.GameMaster, gm, "RAIN", "0");
        Check(gm.NetState.Packets[^1] == ((byte)0, (byte)0, (byte)15), "same-type zero count");
        CommandSystem.Run(AccessLevel.GameMaster, gm, "clear", "70");
        Check(gm.NetState.Packets[^1] == ((byte)255, (byte)0, (byte)15), "clear always sends zero");
        CommandSystem.Run(AccessLevel.GameMaster, gm, "clear", "0");
        Check(gm.NetState.Packets[^1] == ((byte)255, (byte)0, (byte)15), "explicit clear zero");
        int before = gm.NetState.Packets.Count;
        foreach (string bad in new[] { "bad", "", "1.5", "999999999999999999999", "-1", "71" })
        {
            CommandSystem.Run(AccessLevel.GameMaster, gm, "rain", bad);
            Check(gm.NetState.Packets.Count == before, "invalid/overflow/range must send nothing");
        }
        CommandSystem.Run(AccessLevel.GameMaster, gm, "clear", "bad");
        CommandSystem.Run(AccessLevel.GameMaster, gm, "hail", "70");
        CommandSystem.Run(AccessLevel.GameMaster, gm);
        CommandSystem.Run(AccessLevel.GameMaster, gm, "rain", "70", "extra");
        Check(gm.NetState.Packets.Count == before, "invalid mode/arity/clear count");
        CommandSystem.Run(AccessLevel.Player, gm, "rain", "70");
        Check(gm.NetState.Packets.Count == before, "dispatcher respects registered GM minimum");
        Check(other.NetState.Packets.Count == 0, "no other recipient receives a packet");
        Console.WriteLine($"[weather command] PASS {checks}: modes, exact bytes, default/zero/clear, invalid/overflow/range, declared GM gate, caller isolation");
    }
}
namespace Server
{
    public enum AccessLevel { Player, GameMaster }
    public class Mobile
    {
        public Network.NetState NetState = new();
        public void SendMessage(string text) { }
    }
}
namespace Server.Network
{
    public class NetState { public List<(byte,byte,byte)> Packets = new(); }
    public static class Packets
    {
        public static void SendWeather(this NetState ns, byte type, byte count, byte temp) => ns.Packets.Add((type,count,temp));
    }
}
namespace Server.Commands
{
    public class UsageAttribute(string text) : Attribute { }
    public class DescriptionAttribute(string text) : Attribute { }
    public class CommandEventArgs(Mobile mobile, string[] args)
    {
        public Mobile Mobile = mobile;
        public int Length => args.Length;
        public string GetString(int index) => args[index];
    }
    public static class CommandSystem
    {
        public static string Name;
        public static AccessLevel Level;
        static Action<CommandEventArgs> handler;
        public static void Register(string name, AccessLevel level, Action<CommandEventArgs> call)
        { Name = name; Level = level; handler = call; }
        public static void Run(AccessLevel level, Mobile mobile, params string[] args)
        { if (level >= Level) handler(new(mobile, args)); }
    }
}
'''


class WeatherCommandTests(unittest.TestCase):
    def test_command_boundaries(self):
        dotnet = shutil.which("dotnet")
        if not dotnet:
            self.skipTest("run launchers/shard/build.bat to install the shard SDK")
        with tempfile.TemporaryDirectory(prefix="guo-weather-") as tmp:
            root = Path(tmp)
            subprocess.run(["git", "apply", str(HERE / "patches/0007-guo-weather.patch")], cwd=root, check=True)
            (root / "WeatherCommand.csproj").write_text(
                '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
                '<TargetFramework>net8.0</TargetFramework><LangVersion>latest</LangVersion>'
                '</PropertyGroup></Project>', encoding="utf-8")
            (root / "Program.cs").write_text(HARNESS, encoding="utf-8")
            (root / "NuGet.Config").write_text(
                '<configuration><packageSources><clear /></packageSources></configuration>', encoding="utf-8")
            result = subprocess.run([dotnet, "run", "--project", "WeatherCommand.csproj",
                "-p:UseSharedCompilation=false", f"-p:RestoreConfigFile={root / 'NuGet.Config'}"],
                cwd=root, capture_output=True, text=True, timeout=120)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertIn("[weather command] PASS", result.stdout)
            print(result.stdout.strip())


if __name__ == "__main__":
    unittest.main()
