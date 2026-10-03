// SPDX-License-Identifier: BSD-2-Clause
using GUO.Configuration;
using GUO.Game;
using GUO.Resources;

namespace GUO.Localization;

internal static class JournalTranslationCommand
{
    public static void Run(World world, string[] args)
    {
        var settings = Settings.GlobalSettings;
        string resource;
        object[] values = [];
        if (args.Length == 3 && TranslationLanguages.TryResolve(args[1], out var source)
            && TranslationLanguages.TryResolve(args[2], out var target) && source.Code != target.Code)
        {
            world.Journal.ResetTranslation();
            settings.JournalTranslationSource = source.Code;
            settings.JournalTranslationTarget = target.Code;
            settings.JournalTranslationEnabled = true;
            resource = "JournalTranslationEnabled";
            values = [source.Code, target.Code];
        }
        else if (args.Length == 2 && args[1] == "languages")
        {
            foreach (var language in TranslationLanguages.All)
                GameActions.Print(world, $"{language.Code}: {language.Name}");
            return;
        }
        else if (args.Length == 2 && args[1] == "off")
        {
            settings.JournalTranslationEnabled = false;
            world.Journal.ResetTranslation();
            resource = "JournalTranslationDisabled";
        }
        else if (args.Length == 2 && args[1] == "status")
        {
            resource = "JournalTranslationStatus";
            values = [settings.JournalTranslationEnabled, settings.JournalTranslationSource,
                settings.JournalTranslationTarget, world.Journal.TranslationStatus, world.Journal.LastTranslationMilliseconds];
        }
        else resource = "JournalTranslationHelp";
        GameActions.Print(world, string.Format(ResGumps.ResourceManager.GetString(resource), values));
    }
}
