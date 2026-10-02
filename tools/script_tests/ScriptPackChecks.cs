// SPDX-License-Identifier: BSD-2-Clause
using GUO.Store;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class ScriptPackChecks
{
    public static void Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "guo-pack-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        void Write(string id, string version, string source, string target = "client", string runtime = "0.1.0",
            string[]? caps = null, bool dependency = false, string auxiliary = "original")
        {
            string folder = Path.Combine(root, id, version);
            Directory.CreateDirectory(folder);
            var payloads = new Dictionary<string, byte[]> {
                ["preview.png"] = new byte[] { 1 }, ["main.razor"] = Encoding.UTF8.GetBytes(source),
                ["notes.txt"] = Encoding.UTF8.GetBytes(auxiliary) };
            var manifest = new StoreManifest { Schema = "guo/store-pack@2", Id = id, Version = version,
                Kind = "content", Target = target == "shared" ? "combined" : target,
                Title = "Script fixture", Author = "GUO", Licence = "CC0-1.0", Preview = "preview.png",
                MinProfileVersion = 6, Files = payloads.ToDictionary(p => p.Key, p => Hash(p.Value)),
                Dependencies = dependency ? new() { ["support-pack"] = "1.0.0" } : new(),
                Components = new() { new StoreComponent { Id = "main", Type = "script", Target = target,
                    Entry = "main.razor", Language = "razor-ce", Runtime = "guo-razor",
                    RuntimeVersion = runtime, Capabilities = (caps ?? new[] { "client.message" }).ToList() } } };
            StorePack.Validate(manifest);
            foreach (var item in payloads) File.WriteAllBytes(Path.Combine(folder, item.Key), item.Value);
            File.WriteAllBytes(Path.Combine(folder, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(manifest));
        }
        bool Refuses(Action action)
        {
            try { action(); return false; }
            catch (Exception e) when (e is IOException or InvalidDataException or FormatException) { return true; }
        }
        try
        {
            Write("support-pack", "1.0.0", "sysmsg support");
            Write("scripts", "1.0.0", "sysmsg first\npause 10000", dependency: true);
            using var store = new StoreClient("http://127.0.0.1:18865", root, 100);
            var host = new CapabilityProbeHost();
            using var session = new ScriptPackSession(store, host);
            var review = session.Review("scripts", "1.0.0", "main");
            check(host.Calls == 0 && !session.Running && !session.IsApproved(review), "pack review is inert and unapproved");
            check(Refuses(() => session.Enable(review)) && Refuses(() => session.Run(review.Identity)), "approval and enable are separate gates");
            session.Approve(review);
            check(!session.IsEnabled(review.Identity) && host.Calls == 0, "approval alone never enables or runs");
            session.Enable(review);
            check(!session.Running && host.Calls == 0, "enable never starts execution");
            session.Run(review.Identity); session.Tick(0);
            check(host.Calls == 1 && session.Running, "explicit run executes verified approved source");
            session.Disable(review.Identity); session.Tick(25);
            check(!session.Running && !session.IsEnabled(review.Identity), "disable stops managed execution");
            session.Enable(review);
            Write("scripts", "2.0.0", "sysmsg second", dependency: true);
            var update = session.Review("scripts", "2.0.0", "main");
            check(!session.IsApproved(update) && Refuses(() => session.Enable(update)) && session.IsEnabled(review.Identity),
                "update requires fresh approval and failed activation preserves old version");
            session.Approve(update); session.Enable(update); session.Rollback(review.Identity);
            session.Run(review.Identity); session.Tick(0);
            check(session.Running && host.Calls == 2, "rollback restores verified old activation without auto-run");
            Write("support-pack", "1.0.0", "sysmsg support", auxiliary: "changed dependency metadata");
            check(Refuses(() => session.Run(review.Identity)), "dependency auxiliary changes invalidate approval");
            session.Reconcile();
            check(!session.Running && !session.IsEnabled(review.Identity) && !session.IsApproved(review), "reconcile revokes changed dependency closure");
            review = session.Review("scripts", "1.0.0", "main");
            session.Approve(review); session.Enable(review); session.Run(review.Identity);
            store.Uninstall("support-pack", "1.0.0");
            session.Tick(25);
            check(!session.Running && !session.IsEnabled(review.Identity) && !session.IsApproved(review), "dependency uninstall stops and revokes dependants");
            check(Refuses(() => session.Review("scripts", "1.0.0", "main")), "missing dependencies reject review");
            Write("scripts", "3.0.0", "sysmsg third");
            review = session.Review("scripts", "3.0.0", "main");
            File.AppendAllText(Path.Combine(root, "scripts", "3.0.0", "main.razor"), "tamper");
            check(Refuses(() => session.Approve(review)), "tamper between review and approval rejected");
            Write("scripts", "3.0.0", "sysmsg third");
            session.Approve(review); session.Enable(review); session.Run(review.Identity); session.Clear();
            check(!session.Running && !session.IsApproved(review) && !session.IsEnabled(review.Identity), "session cleanup drops execution and approval");
            foreach (string target in new[] { "server", "shared" })
            {
                Write("unsupported", "1.0.0", "sysmsg no", target: target);
                check(Refuses(() => session.Review("unsupported", "1.0.0", "main")), target + " target never executes in client");
            }
            Write("unsupported", "1.0.0", "sysmsg no", runtime: "9.0.0");
            check(Refuses(() => session.Review("unsupported", "1.0.0", "main")), "unsupported runtime version rejected");
            Write("unsupported", "1.0.0", "sysmsg no", caps: new[] { "future.execute" });
            check(Refuses(() => session.Review("unsupported", "1.0.0", "main")), "unsupported declared capability rejected");
            Write("unsupported", "1.0.0", "say no");
            check(Refuses(() => session.Review("unsupported", "1.0.0", "main")), "missing required capability rejected before approval");
        }
        finally { Directory.Delete(root, true); }
    }
}
