// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;

namespace GUO.Localization;

internal sealed record TranslationLanguage(string Code, string Name);

/// <summary>Provider choices, not certification of fonts or native translation quality.</summary>
internal static class TranslationLanguages
{
    public static IReadOnlyList<TranslationLanguage> All { get; } = Array.AsReadOnly(new TranslationLanguage[]
    {
        new("en", "English"),
        new("es", "Spanish"),
        new("pt-BR", "Brazilian Portuguese"),
        new("pt-PT", "European Portuguese"),
        new("fr", "French"),
        new("de", "German"),
        new("it", "Italian"),
        new("ru", "Russian"),
        new("uk", "Ukrainian"),
        new("pl", "Polish"),
        new("tr", "Turkish"),
        new("nl", "Dutch"),
        new("sv", "Swedish"),
        new("da", "Danish"),
        new("nb", "Norwegian Bokmal"),
        new("fi", "Finnish"),
        new("cs", "Czech"),
        new("ro", "Romanian"),
        new("hu", "Hungarian"),
        new("el", "Greek"),
        new("bg", "Bulgarian"),
        new("sr", "Serbian"),
        new("hr", "Croatian"),
        new("sk", "Slovak"),
        new("zh-Hans", "Simplified Chinese"),
        new("zh-Hant", "Traditional Chinese"),
        new("ja", "Japanese"),
        new("ko", "Korean"),
        new("hi", "Hindi"),
        new("bn", "Bengali"),
        new("ur", "Urdu"),
        new("pa", "Punjabi"),
        new("mr", "Marathi"),
        new("gu", "Gujarati"),
        new("ta", "Tamil"),
        new("te", "Telugu"),
        new("kn", "Kannada"),
        new("ml", "Malayalam"),
        new("ne", "Nepali"),
        new("si", "Sinhala"),
        new("id", "Indonesian"),
        new("ms", "Malay"),
        new("tl", "Tagalog"),
        new("vi", "Vietnamese"),
        new("th", "Thai"),
        new("my", "Burmese"),
        new("km", "Khmer"),
        new("ar", "Standard Arabic"),
        new("fa", "Persian"),
        new("he", "Hebrew"),
        new("sw", "Swahili"),
        new("az", "North Azerbaijani"),
        new("uz", "Northern Uzbek"),
        new("kk", "Kazakh"),
    });

    public static bool TryResolve(string input, out TranslationLanguage language)
    {
        language = null;
        if (string.IsNullOrWhiteSpace(input)) return false;
        string code = input.Trim().Replace('_', '-').ToLowerInvariant();
        code = code switch
        {
            "pt" => "pt-br",
            "zh" or "zh-cn" or "zh-sg" => "zh-hans",
            "zh-tw" or "zh-hk" or "zh-mo" => "zh-hant",
            "fil" or "fil-ph" => "tl",
            "no" or "no-no" => "nb",
            "iw" => "he",
            _ => code
        };
        foreach (var item in All)
            if (string.Equals(item.Code, code, StringComparison.OrdinalIgnoreCase))
            {
                language = item;
                return true;
            }
        // Only recognised languages with a two-letter region may fall back.
        // Keep Chinese script and Portuguese regional choices explicit.
        int dash = code.IndexOf('-');
        if (dash > 0 && code.Length == dash + 3 && char.IsAsciiLetter(code[dash + 1])
            && char.IsAsciiLetter(code[dash + 2]) && code[..dash] is not ("zh" or "pt"))
            return TryResolve(code[..dash], out language);
        return false;
    }
}
