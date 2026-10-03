// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using GUO.Configuration;
using GUO.Game.Managers;
using GUO.Utility.Logging;

namespace GUO.Localization;

/// <summary>
/// Game-thread owner of the opt-in journal translation session. JournalManager holds
/// one of these and forwards to it, so the ported file carries hooks, not logic.
/// Requests go to a worker; results are applied from Update on the game thread.
/// </summary>
internal sealed class JournalTranslationHost
{
    private JournalTranslationSession _session;
    private bool _initialized;
    private string _label;

    /// <summary>Bumped whenever a translation lands, so journal views know to rebuild.</summary>
    public static int Revision { get; private set; }
    public string Status { get; private set; } = "off";
    public long LastMilliseconds { get; private set; }

    public void Queue(JournalEntry entry)
    {
        if (!Settings.GlobalSettings.JournalTranslationEnabled) return;
        Ensure();
        if (_session != null)
            Status = _session.TryQueue(new(entry.Id, entry.Text, entry.Name)) ? "pending" : "queue full or text skipped";
    }

    public void Update(IEnumerable<JournalEntry> entries, ISet<string> ignored)
    {
        if (!Settings.GlobalSettings.JournalTranslationEnabled)
        {
            if (_session != null) Reset();
            return;
        }
        while (_session != null && _session.TryTake(out var result))
        {
            Status = result.Status;
            LastMilliseconds = result.Milliseconds;
            if (result.Status != "translated") continue;
            foreach (JournalEntry entry in entries)
            {
                // Entries are recycled; object identity alone is not safe.
                if (entry.Id != result.Id || ignored.Contains(entry.Name)) continue;
                entry.Translation = _label + result.Text;
                Revision++;
                break;
            }
        }
    }

    public void Reset()
    {
        _session?.Dispose();
        _session = null;
        _initialized = false;
        Status = "off";
    }

    private void Ensure()
    {
        if (_initialized) return;
        _initialized = true;
        var settings = Settings.GlobalSettings;
        try
        {
            var provider = new OllamaJournalTranslator(settings.JournalTranslationEndpoint,
                settings.JournalTranslationModel, settings.JournalTranslationSource,
                settings.JournalTranslationTarget, settings.JournalTranslationGlossary);
            _session = new(provider, TimeSpan.FromSeconds(Math.Clamp(settings.JournalTranslationTimeoutSeconds, 1, 60)));
            TranslationLanguages.TryResolve(settings.JournalTranslationSource, out var source);
            TranslationLanguages.TryResolve(settings.JournalTranslationTarget, out var target);
            _label = string.Format(GUO.Resources.ResGumps.ResourceManager.GetString(
                "JournalTranslationLabel"), source.Code, target.Code);
            Status = "ready";
        }
        catch (ArgumentException)
        {
            Status = "invalid configuration";
            Log.Warn("Journal translation disabled: invalid local provider configuration.");
        }
    }
}
