#if TOOLS
namespace GUO.Editor;
using System;
using System.Collections.Generic;
using System.IO;
using Godot;

/// <summary>Offline authentication regression. Only a local Python fixture runs; no browser or provider is contacted.</summary>
public partial class AiSetupAuthProbe : Node
{
    public override async void _Ready()
    {
        var transcript = new List<string>();
        int pages = 0;
        AiFeatures.Apply(true);
        try
        {
            Require(AgentSession.TrySignInPage("Open https://accounts.google.com/o/oauth2/auth?state=synthetic", out _), "Google HTTPS sign-in accepted");
            foreach (string unsafeUrl in new[] { "https://accounts.google.com.attacker.invalid/", "http://accounts.google.com/", "https://attacker@accounts.google.com/", "https://accounts.google.com:444/" })
                Require(!AgentSession.TrySignInPage(unsafeUrl, out _), "untrusted sign-in URL refused");
            Require(!AiSetupTab.SubscriptionMethod("agent-platform") && !AiSetupTab.SubscriptionMethod("gemini-api-key"), "API/ADC excluded from subscription methods");
            string python = QueueClient.FindPython() ?? throw new InvalidOperationException("Python is required for the local fixture");
            string root = EditorData.RepoRoot;
            using var agent = new AgentSession(AgentCatalog.Antigravity, "Synthetic sign-in");
            agent.Transcript += line => transcript.Add(line.Text);
            agent.SignInPage += _ => pages++; // Deliberately no OS.ShellOpen in a test.
            bool ready = await agent.StartAsync(python, new[] { Path.Combine(root, "tools/ai_hub/fake_acp_agent.py"), "--auth" }, root);
            Require(!ready && !agent.Ready, "unauthenticated session cannot be ready");
            bool refused = false;
            try { await agent.SignInAsync("unadvertised-method", root); }
            catch (InvalidOperationException) { refused = true; }
            Require(refused, "unadvertised sign-in method refused");
            Require(await agent.SignInAsync("oauth-personal", root) && agent.Ready, "explicit sign-in opens a ready session");
            Require(pages == 1, "sign-in URL reaches host browser action once");
            Require(!string.Join('\n', transcript).Contains("state=synthetic"), "OAuth state omitted from transcript");
            AiFeatures.Apply(false);
            Require(!agent.Ready, "disabling AI closes the signed-in session");
            GD.Print("[AI setup auth] PASS: 12 checks; local fixture only; no browser, provider request or inference");
            GetTree().Quit();
        }
        catch (Exception ex)
        {
            GD.PrintErr("[AI setup auth] FAIL: " + ex);
            GetTree().Quit(1);
        }
        finally { AiFeatures.Apply(false); }
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
#endif
