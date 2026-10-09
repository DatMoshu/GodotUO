// Runs the patched AccountPrompt.Initialize on accounts named on the command
// line ("name:password:Level", each with one character), with UO_SHARD_* taken
// from the environment, then prints every account and every log line.
using System;
using System.Linq;
using Server;
using Server.Accounting;
using Server.Logging;
using Server.Misc;

public static class Program
{
    public static int Main(string[] args)
    {
        foreach (var spec in args)
        {
            var parts = spec.Split(':');
            var account = new Account(parts[0], parts[1]) { AccessLevel = Enum.Parse<AccessLevel>(parts[2]) };
            account.Add(new Mobile { Name = parts[0] + "-char", AccessLevel = account.AccessLevel });
        }

        AccountPrompt.Initialize();

        foreach (var a in Accounts.All.Values.OrderBy(a => a.Username))
        {
            var chars = string.Join(",", Enumerable.Range(0, a.Length).Select(i => a[i].AccessLevel));
            var prot = ServerAccess.Protected.Contains(a.Username) ? " protected" : "";
            Console.WriteLine($"account {a.Username} {a.AccessLevel} password={a.PlainPassword} chars={chars}{prot}");
        }

        foreach (var line in LogFactory.Lines)
        {
            Console.WriteLine(line);
        }

        return 0;
    }
}
