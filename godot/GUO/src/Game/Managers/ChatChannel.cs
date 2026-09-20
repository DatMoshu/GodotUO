// SPDX-License-Identifier: BSD-2-Clause

namespace GUO.Game.Managers
{
    internal sealed class ChatChannel
    {
        public ChatChannel(string name, bool hasPassword)
        {
            Name = name;
            HasPassword = hasPassword;
        }

        public readonly bool HasPassword;

        public readonly string Name;
    }
}