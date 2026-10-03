// SPDX-License-Identifier: BSD-2-Clause
using GUO.Game.Data;

namespace GUO.Localization;

internal static class JournalTranslationPolicy
{
    public static bool CanTranslate(MessageType type, string text, bool receivedSpeech, bool isMobile, bool ignored)
        => receivedSpeech && isMobile && !ignored && !string.IsNullOrWhiteSpace(text)
            && text.Length <= 1024
            && (type == MessageType.Regular || type == MessageType.Emote || type == MessageType.Yell)
            && !text.TrimStart().StartsWith('[') && !text.TrimStart().StartsWith('/');
}
