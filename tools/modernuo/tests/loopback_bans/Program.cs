// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Net;
using Server;
using Server.Network;
using Server.Network.Bans;

static class Program
{
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    static void Main(string[] args)
    {
        bool vulnerable = args.Length > 0 && args[0] == "--unpatched";
        var reporter = new RecordingReporter();
        AutoDenylist.LoadForTesting(true, 900_000, 100);
        BanChannel.ConfigureForTesting(new IBanReporter[] { new AutoDenylistReporter(), reporter });
        BanExemptions.Configure();

        // This is the real report -> exemption -> denylist -> accept-filter path.
        foreach (string literal in new[] { "127.0.0.1", "127.0.0.2", "::1", "::ffff:127.0.0.1" })
        {
            var ip = IPAddress.Parse(literal);
            foreach (string reason in new[] { BanReasons.SilentConnect, BanReasons.InvalidSeed,
                         BanReasons.ForeignProtocol, BanReasons.RateLimit })
            {
                BanChannel.Report(ip, TimeSpan.FromHours(4), reason);
                Check(new AutoDenylistFilter().ShouldDeny(ip) == vulnerable,
                    "loopback behavioral report has unexpected accept result");
            }
        }
        Check((reporter.Reports > 0) == vulnerable, "loopback contribution reached external reporter");
        if (vulnerable)
        {
            Core.TickCount = 899_999;
            Check(AutoDenylist.IsDenied(IPAddress.Loopback), "hold expired before 15 minutes");
            Core.TickCount = 900_000;
            Check(!AutoDenylist.IsDenied(IPAddress.Loopback), "hold did not expire at 15 minutes");
            Core.TickCount = 0;
        }

        // The narrow fix must preserve public and LAN flood protection.
        foreach (string literal in new[] { "203.0.113.9", "192.168.1.9" })
        {
            var ip = IPAddress.Parse(literal);
            BanChannel.Report(ip, TimeSpan.FromHours(4), BanReasons.SilentConnect);
            Check(new AutoDenylistFilter().ShouldDeny(ip), "non-loopback protection was bypassed");
        }

        int before = reporter.Reports;
        BanChannel.Report(IPAddress.Loopback, TimeSpan.FromHours(4), BanReasons.Manual);
        BanChannel.Report(IPAddress.IPv6Loopback, TimeSpan.FromHours(4), "future-reason");
        Check(reporter.Reports == before + 2, "manual or unknown reason was suppressed");
        Check(!BanExemptions.IsExempt(null, BanReasons.SilentConnect), "null address exempted");

        if (!vulnerable)
        {
            int strikes = 0;
            Check(BanExemptions.IsExempt(IPAddress.Loopback, BanReasons.SilentConnect,
                (_, _) => { strikes++; return false; }), "loopback exemption missing");
            Check(strikes == 0, "loopback spent earned allowlist strikes");
        }
        Console.WriteLine(vulnerable
            ? "REPRODUCED: one silent loopback report denies local accepts for 15 minutes"
            : "PASS: loopback accepts survive reports; manual bans and remote protection retained");
    }

    sealed class RecordingReporter : IBanReporter
    {
        public int Reports;
        public string Name => "recording";
        public bool CanRetract => true;
        public void Register() { }
        public void Start(System.Threading.CancellationToken token) { }
        public void Stop() { }
        public void Report(IPAddress address, TimeSpan ttl, string reason) => Reports++;
        public void Retract(IPAddress address) { }
    }
}
