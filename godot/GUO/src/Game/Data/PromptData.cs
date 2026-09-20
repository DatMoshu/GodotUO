// SPDX-License-Identifier: BSD-2-Clause

namespace GUO.Game.Data
{
    internal enum ConsolePrompt
    {
        None,
        ASCII,
        Unicode
    }

    internal readonly struct PromptData(ConsolePrompt prompt, ulong data)
    {
        public readonly ConsolePrompt Prompt = prompt;
        public readonly ulong Data = data;
    }
}