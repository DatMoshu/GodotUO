// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using Godot;
using GUO.Game;
using GUO.Game.Managers;
using GUO.Game.Scripting;
using GUO.Input.Touch.Modern;
using GUO.Input.Touch;
using GUO.Store;
using GUO.Configuration;

namespace GUO.Host;

/// <summary>Live editor input and execution check on the dev shard, in a scratch profile.</summary>
internal static class ScriptsProbe
{
    public static async Task<bool> Run(Node host, string screenshotDir, string screenshotName)
    {
        bool passed = true;
        void Check(bool ok, string name)
        {
            GD.Print($"[GUO] scripts check: {(ok ? "ok" : "FAIL")} {name}");
            passed &= ok;
        }
        bool touch = TouchInput.Enabled;
        TouchInput.Enabled = false;
        InputProbe.PointerScale = Client.Game.DpiScale;
        await InputProbe.EnterTheWorld(host, 200);
        InputProbe.PointerScale = 1;
        TouchInput.Enabled = touch;
        World world = Client.Game.UO.World;
        if (!world.InGame) { Check(false, "logged in"); return false; }
        Client.Game._Input(new InputEventKey { Keycode = Key.R, CtrlPressed = true, ShiftPressed = true, Pressed = true });
        await InputProbe.Wait(host, 40);
        var panel = ModernGump.Current as ModernScripts;
        Check(panel != null && ModernGump.IsOpen, "shortcut opens native editor");
        if (panel == null) return false;
        var editor = Find<CodeEdit>(panel);
        var name = Find<LineEdit>(panel);
        Check(editor != null && name != null, "editor controls exist");
        if (editor == null || name == null) return false;
        foreach (var starter in ScriptCatalog.Starters)
        {
            Check(world.Scripts.Start(starter.Source), "starter validates: " + starter.Id);
            world.Scripts.Stop();
        }

        async Task Click(Control control)
        {
            // Containers lay out asynchronously after a popup/list changes visibility.
            await InputProbe.Wait(host, 3);
            Vector2 point = panel.CentreOf(control) * Client.Game.DpiScale;
            if (touch)
            {
                Client.Game._Input(new InputEventScreenTouch { Index = 0, Position = point, Pressed = true });
                Client.Game._Input(new InputEventScreenTouch { Index = 0, Position = point, Pressed = false });
                await InputProbe.Wait(host, 3);
                return;
            }
            Client.Game._Input(new InputEventMouseMotion { Position = point, GlobalPosition = point });
            Client.Game._Input(new InputEventMouseButton { Position = point, GlobalPosition = point, ButtonIndex = MouseButton.Left, Pressed = true });
            Client.Game._Input(new InputEventMouseButton { Position = point, GlobalPosition = point, ButtonIndex = MouseButton.Left, Pressed = false });
            await InputProbe.Wait(host, 3);
        }
        Button Button(string text) => Find<Button>(panel, b => b.Text == text);
        Check(panel.Rect.HasPoint(panel.CentreOf(Button("Run"))), "Run is visible before interaction");
        editor.Text = "";
        await Click(editor);
        Client.Game._Input(new InputEventKey { Keycode = Key.X, Unicode = 'x', Pressed = true });
        Check(editor.Text == "x", "mouse focus and keyboard text stay inside editor");

        const string marker = "GUO embedded script verified";
        editor.Text = "sysmsg '" + marker + "'\npause 100\nsysmsg 'Finished'";
        name.Text = "probe";
        await Click(Button("Save"));
        editor.Text = "changed";
        await Click(Button("Load"));
        Check(editor.Text == "changed", "load protects unsaved edits");
        await Click(Button("Load"));
        Check(editor.Text.Contains(marker), "saved script loads after confirmation");
        Check(editor.SyntaxHighlighter is CodeHighlighter, "syntax colors attached to editor");
        string beforeStarter = editor.Text;
        await Click(Button("Starter scripts"));
        await InputProbe.Wait(host, 20);
        Check(Button("Add to my scripts").IsVisibleInTree() && panel.Rect.HasPoint(panel.CentreOf(Button("Add to my scripts"))), "starter popup and Add button fit the card");
        var starterScroll = Find<ScrollContainer>(panel, c => c.IsVisibleInTree());
        Check(starterScroll != null && starterScroll.Size.Y >= 72, "starter list retains visible browsing space");
        if (!string.IsNullOrWhiteSpace(screenshotDir))
        {
            Directory.CreateDirectory(screenshotDir);
            using var shot = host.GetViewport().GetTexture().GetImage();
            Check(shot.SavePng(Path.Combine(screenshotDir, screenshotName + "-starters.png")) == Error.Ok, "starter popup screenshot saved");
        }
        await Click(Button("Add to my scripts"));
        Check(editor.Text == beforeStarter && !world.Scripts.Running, "adding a starter preserves current edits and never runs it");
        var library = Find<OptionButton>(panel);
        Check(library.ItemCount == 2 && library.GetItemText(library.Selected) == "welcome", "starter becomes a personal library entry");

        editor.Text = "ca";
        editor.SetCaretColumn(2);
        await Click(Button("Suggest"));
        if (touch || panel.Rect.Size.X < 760)
        {
            var suggestions = Find<ItemList>(panel, list => list.IsVisibleInTree() && list.ItemCount == 1 && list.GetItemText(0) == "cast");
            Check(suggestions != null, "touch command suggestions available");
            if (suggestions != null)
            {
                Vector2 point = panel.Rect.Position * Client.Game.DpiScale +
                    suggestions.GetGlobalTransform() * suggestions.GetItemRect(0).GetCenter();
                Client.Game._Input(new InputEventScreenTouch { Index = 0, Position = point, Pressed = true });
                Client.Game._Input(new InputEventScreenTouch { Index = 0, Position = point, Pressed = false });
                await InputProbe.Wait(host, 3);
            }
        }
        else
        {
            Check(editor.GetCodeCompletionOptions().Count > 0, "native command completion available");
            editor.ConfirmCodeCompletion();
        }
        Check(editor.Text == "cast", "accepting completion replaces partial command");
        editor.Text = beforeStarter;
        await Click(Button("Run"));
        await InputProbe.Wait(host, 60);
        Check(world.Scripts.Status == "Completed", "Run button executes to completion");
        Check(JournalManager.Entries.Any(entry => entry.Text == marker), "script emits a real journal entry");

        editor.Text = "pause 30000\nsysmsg 'must not run'";
        await Click(Button("Run"));
        Check(world.Scripts.Running, "waiting script remains active without blocking UI");
        Client.Game._Input(new InputEventKey { Keycode = Key.F12, CtrlPressed = true, ShiftPressed = true, Pressed = true });
        Check(!world.Scripts.Running, "emergency shortcut stops while editor has focus");
        await InputProbe.Wait(host, 20);
        editor.Text = "sysmsg 'must not run'\nunknown_command";
        await Click(Button("Run"));
        Check(!world.Scripts.Running && world.Scripts.Status.StartsWith("Line 2:"), "invalid script rejected through UI");
        Check(!JournalManager.Entries.Any(entry => entry.Text == "must not run"), "cancelled and invalid scripts have no later side effects");

        // Opt-in fixture: use --store-install guo-razor-starters against a local seeded store.
        if (System.Environment.GetEnvironmentVariable("GUO_SCRIPT_PACK_PROBE") == "1")
        {
            panel.Close();
            StoreWindow.Open();
            await InputProbe.Wait(host, 100);
            var store = Find<StoreWindow>(Client.Game);
            var browse = store == null ? null : Find<Button>(store, b => b.Text == "Browse scripts");
            Check(browse != null && !browse.Disabled, "installed script pack exposes Browse scripts in Store");
            async Task Capture(string suffix)
            {
                await InputProbe.Wait(host, 12);
                if (string.IsNullOrWhiteSpace(screenshotDir)) return;
                using var shot = host.GetViewport().GetTexture().GetImage();
                Check(shot.SavePng(Path.Combine(screenshotDir, screenshotName + suffix + ".png")) == Error.Ok, "store evidence saved: " + suffix);
            }
            await Capture("-store");
            if (browse == null) { store?.QueueFree(); return false; }
            Vector2 point = browse.GetGlobalRect().GetCenter();
            host.GetViewport().PushInput(new InputEventMouseMotion { Position = point, GlobalPosition = point }, true);
            host.GetViewport().PushInput(new InputEventMouseButton { Position = point, GlobalPosition = point, ButtonIndex = MouseButton.Left, Pressed = true }, true);
            host.GetViewport().PushInput(new InputEventMouseButton { Position = point, GlobalPosition = point, ButtonIndex = MouseButton.Left, Pressed = false }, true);
            await InputProbe.Wait(host, 20);
            Check(!StoreWindow.IsOpen && ModernGump.IsOpen, "Store browse action opens script pack preview");
            var choice = Find<Button>(panel, b => b.Text.StartsWith("scripts/welcome.razor"));
            Check(choice != null && choice.IsVisibleInTree(), "installed scripts appear in the filtered library");
            if (choice == null) return false;
            string edits = editor.Text;
            int count = library.ItemCount;
            world.Scripts.Start("pause 30000\nsysmsg 'must not run'");
            await Click(choice);
            Check(world.Scripts.Running, "pack compatibility preview preserves the active runner");
            var preview = Find<CodeEdit>(panel, c => c != editor);
            Check(preview.Text.Contains("Hello from your GUO script pack"), "store source is previewed after verification");
            await Capture("-store-library");
            var approve = Find<Button>(panel, b => b.Text == "Approve");
            if (approve?.IsVisibleInTree() == true)
            {
                Check(Button("Enable").Disabled && Button("Run pack").Disabled, "managed execution starts unapproved");
                await Click(approve);
                Check(!Button("Enable").Disabled && !world.PackScripts.Running, "approval is separate from enable and execution");
                await Click(Button("Enable"));
                Check(!Button("Run pack").Disabled && !world.PackScripts.Running, "enable never starts execution");
                await Capture("-managed-approved");
                Check(Button("Rollback").GetGlobalRect().End.Y <= host.GetViewport().GetVisibleRect().End.Y - 8,
                    "managed controls remain fully inside visible viewport");
                await Click(Button("Run pack"));
                await InputProbe.Wait(host, 10);
                Check(JournalManager.Entries.Any(entry => entry.Text == "Hello from your GUO script pack"), "explicit managed Run executes source");
                await Click(Button("Disable"));
                Check(!world.PackScripts.Running && Button("Run pack").Disabled, "managed Disable stops execution");
                await Click(Button("Revoke"));
                Check(!approve.Disabled && Button("Enable").Disabled, "managed Revoke removes approval");
                world.Scripts.Start("pause 30000\nsysmsg 'must not run'");
            }
            await Click(Button("Add to my scripts"));
            Check(library.ItemCount == count + 1 && editor.Text == edits && world.Scripts.Running, "store import adds a copy without replacing edits or execution");
            string copy = library.GetItemText(library.Selected);
            Check(File.Exists(Path.Combine(ProfileManager.ProfilePath, "scripts", "script-" + copy + ".razor.LICENSE.txt")), "store attribution retained beside personal script");
            world.Scripts.Stop();
            Check(!world.PackScripts.Running, "store import never starts managed execution");
        }

        // Leave the useful example open for CaptureFrame, including its real completion state.
        editor.Text = "// Runs inside GUO\nsysmsg '" + marker + "'\npause 1000\nsysmsg 'Finished'";
        await InputProbe.Wait(host, 20);
        await Click(Button("Run"));
        await InputProbe.Wait(host, 150);
        Check(panel.Rect.HasPoint(panel.CentreOf(Button("Run"))), "Run remains inside visible card after execution");
        GD.Print($"[GUO] scripts layout: card={panel.Rect}, editor={editor.GetGlobalRect()}, run={panel.CentreOf(Button("Run"))}");
        return passed;
    }

    private static T Find<T>(Node node, Func<T, bool> predicate = null) where T : Node
    {
        if (node is T match && (predicate == null || predicate(match))) return match;
        foreach (Node child in node.GetChildren())
        {
            T found = Find(child, predicate);
            if (found != null) return found;
        }
        return null;
    }
}
