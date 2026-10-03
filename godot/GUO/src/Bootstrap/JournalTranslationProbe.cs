// SPDX-License-Identifier: BSD-2-Clause
using Godot;
using GUO.Configuration;
using GUO.Game;
using GUO.Game.Data;
using GUO.Game.Managers;
using GUO.Game.UI.Gumps;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace GUO.Host;

/// <summary>Real shard speech round trips and real local Ollama; no canned translations.</summary>
internal static class JournalTranslationProbe
{
    public static async Task<bool> Run(Node host, string output)
    {
        var evidence = new List<object>();
        string folder = string.IsNullOrEmpty(output) ? ProjectSettings.GlobalizePath("user://translation-probe") : output;
        Directory.CreateDirectory(folder);
        try
        {
            await InputProbe.EnterTheWorld(host, 200);
            var world = Client.Game.UO.World;
            if (!world.InGame) throw new InvalidOperationException("Did not enter the dev shard.");
            var settings = Settings.GlobalSettings;
            string endpoint = settings.JournalTranslationEndpoint;
            evidence.Add(new { provider = "Ollama", model = settings.JournalTranslationModel, platform = OS.GetName(), syntheticSpeech = true });
            // This probe must be run with an isolated --cache-dir, as documented.
            ProfileManager.CurrentProfile.SaveJournalToFile = false;
            Settings.GlobalSettings.JournalTranslationTimeoutSeconds = 60;
            settings.JournalTranslationEnabled = true;
            foreach (var gump in UIManager.Gumps.ToArray())
                if (gump is JournalGump || gump is ResizableJournal || gump is PaperDollGump) gump.Dispose();
            JournalManager.Entries.Clear();
            var journal = new ResizableJournal(world) { X = 24, Y = 80 };
            journal.ResizeWindow(new GUO.Compat.Point(560, 470));
            UIManager.Add(journal);
            await InputProbe.Wait(host, 30);

            async Task<JournalEntry> Speak(string source, MessageType type, string shot)
            {
                long before = JournalManager.Entries.Count == 0 ? 0 : JournalManager.Entries.Last().Id;
                var elapsed = Stopwatch.StartNew();
                GameActions.Say(source, type: type);
                JournalEntry entry = null;
                while (elapsed.Elapsed < TimeSpan.FromSeconds(8))
                {
                    entry = JournalManager.Entries.FirstOrDefault(e => e.Id > before && e.Text.Contains(source.Trim('*'), StringComparison.Ordinal));
                    if (entry != null) break;
                    await InputProbe.Wait(host, 1);
                }
                if (entry == null) throw new InvalidOperationException("No server echo for synthetic speech.");
                string original = entry.Text;
                int frames = 0;
                while (entry.Translation == null && elapsed.Elapsed < TimeSpan.FromSeconds(65))
                {
                    await InputProbe.Wait(host, 1);
                    frames++;
                }
                if (entry.Translation == null) throw new InvalidOperationException("Translation failed: " + world.Journal.TranslationStatus);
                if (entry.Text != original) throw new InvalidOperationException("Original changed.");
                evidence.Add(new { source = original, translation = entry.Translation, id = entry.Id,
                    milliseconds = world.Journal.LastTranslationMilliseconds, gameFramesWhileWaiting = frames, serverEcho = true });
                GD.Print($"[GUO] translation probe: {settings.JournalTranslationSource}>{settings.JournalTranslationTarget} result in {world.Journal.LastTranslationMilliseconds} ms; original preserved");
                if (shot != null) await Shot(host, folder, shot);
                return entry;
            }

            settings.JournalTranslationGlossary = new() { ["Luna Nera"] = "Black Moon", ["Renval"] = "Renval" };
            world.CommandManager.Execute("translate", "translate", "it", "en");
            var glossaryEntry = await Speak("Ci vediamo alla Luna Nera, Renval.", MessageType.Regular, null);
            if (!glossaryEntry.Translation.Contains("Black Moon")) throw new InvalidOperationException("Glossary failed.");
            await Speak("*Si avvicina alla taverna e saluta Renval.*", MessageType.Emote, "italian-to-english");

            settings.JournalTranslationGlossary = new() { ["Black Moon"] = "Luna Nera", ["Renval"] = "Renval" };
            world.CommandManager.Execute("translate", "translate", "en", "it");
            await Speak("Meet me at the Black Moon, Renval.", MessageType.Regular, null);
            await Speak("*Opens the door and greets Renval.*", MessageType.Emote, "english-to-italian");

            journal.Dispose();
            var classic = new JournalGump(world) { X = 40, Y = 60, Height = 520 };
            UIManager.Add(classic);
            await Shot(host, folder, "classic-journal");
            classic.Dispose();
            journal = new ResizableJournal(world) { X = 24, Y = 80 };
            journal.ResizeWindow(new GUO.Compat.Point(560, 600));
            UIManager.Add(journal);

            // Failure is a real local refused connection; original remains readable.
            settings.JournalTranslationEndpoint = "http://127.0.0.1:1/api/chat";
            world.Journal.ResetTranslation();
            GameActions.Say("The original remains readable when translation is unavailable.");
            var timer = Stopwatch.StartNew();
            while (world.Journal.TranslationStatus != "unavailable" && timer.Elapsed < TimeSpan.FromSeconds(8))
                await InputProbe.Wait(host, 1);
            if (world.Journal.TranslationStatus != "unavailable") throw new InvalidOperationException("Failure fallback not observed.");
            var fallback = JournalManager.Entries.Last();
            if (fallback.Translation != null || !fallback.Text.StartsWith("The original remains"))
                throw new InvalidOperationException("Failure modified original.");
            evidence.Add(new { fallback = "unavailable", originalPreserved = true });
            await Shot(host, folder, "provider-unavailable");

            world.CommandManager.Execute("translate", "translate", "off");
            if (settings.JournalTranslationEnabled || world.Journal.TranslationStatus != "off")
                throw new InvalidOperationException("Off command failed.");
            journal.Dispose();

            // Reuse a real journal object while its original message is in flight.
            settings.JournalTranslationEndpoint = endpoint;
            settings.JournalTranslationGlossary = new() { ["Renval"] = "Renval" };
            world.CommandManager.Execute("translate", "translate", "it", "en");
            world.Journal.Add("Ciao Renval.", 0, "Localizer", world.Player.Serial, TextType.OBJECT, translate: true);
            var recycled = JournalManager.Entries.Last();
            long oldId = recycled.Id;
            for (int i = 0; i <= Constants.MAX_JOURNAL_HISTORY_COUNT; i++)
                world.Journal.Add("Synthetic eviction test " + i, 0, "", null, TextType.CLIENT);
            if (recycled.Id == oldId) throw new InvalidOperationException("Eviction test did not recycle entry.");
            timer.Restart();
            while (world.Journal.TranslationStatus == "pending" && timer.Elapsed < TimeSpan.FromSeconds(65))
                await InputProbe.Wait(host, 1);
            if (world.Journal.TranslationStatus != "translated" || JournalManager.Entries.Any(e => e.Translation != null))
                throw new InvalidOperationException("Late translation attached to recycled entry or provider failed.");
            evidence.Add(new { recycledEntryProtection = true });

            world.Journal.Add("Buona sera, Renval.", 0, "Localizer", world.Player.Serial, TextType.OBJECT, translate: true);
            world.Journal.Clear(); // the same lifecycle hook World.Clear uses on disconnect
            await InputProbe.Wait(host, 120);
            if (JournalManager.Entries.Any(e => e.Translation != null))
                throw new InvalidOperationException("Translation survived session clear.");
            evidence.Add(new { sessionClearCancels = true, offCommand = true });
            GD.Print("[GUO] translation probe: PASS real server echoes, two directions, glossary, emotes, fallback, entry reuse and session clear");
            File.WriteAllText(Path.Combine(folder, "evidence.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch (Exception ex)
        {
            GD.PrintErr("[GUO] translation probe: FAIL " + ex.Message);
            await Shot(host, folder, "failure");
            return false;
        }
        finally
        {
            Settings.GlobalSettings.JournalTranslationEnabled = false;
            Client.Game?.UO?.World?.Journal.ResetTranslation();
        }
    }

    private static async Task Shot(Node host, string folder, string name)
    {
        await InputProbe.Wait(host, 30);
        await host.ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
        using var frame = host.GetViewport().GetTexture().GetImage();
        if (frame.SavePng(Path.Combine(folder, name + ".png")) != Error.Ok)
            throw new IOException("Could not save screenshot.");
    }
}
