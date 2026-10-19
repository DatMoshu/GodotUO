// SPDX-License-Identifier: BSD-2-Clause
using System;

namespace GUO.ModApi
{
    /// <summary>The ABI this assembly describes. A mod's <c>mod.json</c> names it in <c>abi</c>;
    /// the client refuses a mod built for another one.</summary>
    public static class ModAbi
    {
        public const int Version = 1;

        /// <summary>A client version as the client encodes it: major, minor, build, revision,
        /// one byte each. <c>Encode(7, 0, 15, 1)</c> is 7.0.15.1.</summary>
        public static uint Encode(int major, int minor, int build, int revision) =>
            (uint)(((major & 0xFF) << 24) | ((minor & 0xFF) << 16) | ((build & 0xFF) << 8) | (revision & 0xFF));
    }

    /// <summary>A mod's entry type, named by <c>entry</c> in <c>mod.json</c>. It needs a public
    /// parameterless constructor. <see cref="Register"/> runs once, when the client loads mods at
    /// start; it declares packets and must not do anything else (no threads, files or sockets).</summary>
    public interface IGuoMod
    {
        void Register(IModRegistrar registrar);
    }

    /// <summary>What <see cref="IGuoMod.Register"/> may call.</summary>
    public interface IModRegistrar
    {
        /// <summary>The mod's id from <c>mod.json</c>.</summary>
        string ModId { get; }

        /// <summary>The client version this client speaks, encoded as <see cref="ModAbi.Encode"/>.</summary>
        uint ClientVersion { get; }

        /// <summary>Registers one packet. Throws when the packet is not declared in
        /// <c>mod.json</c>, when another mod already has it, or when it would replace one of the
        /// client's own handlers without <see cref="PacketRegistration.Override"/>.</summary>
        void AddPacket(PacketRegistration registration);
    }

    /// <summary>Handles one framed packet: <paramref name="packet"/> is the whole packet,
    /// id and length bytes included, exactly as the server sent it. Runs on the game thread.
    /// An exception drops that packet and is logged; the client carries on.</summary>
    public delegate void PacketHandler(IModContext context, ReadOnlySpan<byte> packet);

    /// <summary>One packet a mod handles.</summary>
    public sealed class PacketRegistration
    {
        /// <summary>The packet id, 0x00..0xFF.</summary>
        public byte Id { get; set; }

        /// <summary>For id 0xBF only: the extended-command subcommand (the big-endian ushort after
        /// the length). Null registers the whole id.</summary>
        public ushort? Subcommand { get; set; }

        /// <summary>The packet's length in bytes, or -1 for a dynamic packet whose bytes 1..2
        /// carry its length. Ignored for a subcommand, which is framed as 0xBF already is.</summary>
        public short Length { get; set; } = -1;

        /// <summary>True when this packet replaces one the client already handles. Must match
        /// <c>override</c> in <c>mod.json</c>.</summary>
        public bool Override { get; set; }

        /// <summary>Lowest client version the registration applies to (inclusive); 0 is any.</summary>
        public uint MinClientVersion { get; set; }

        /// <summary>Highest client version the registration applies to (inclusive); 0 is any.</summary>
        public uint MaxClientVersion { get; set; }

        public PacketHandler Handler { get; set; }
    }

    /// <summary>What a packet handler may do in the client. Deliberately small: a mod that needs
    /// more asks for it in a new ABI version rather than reaching into the client.</summary>
    public interface IModContext
    {
        /// <summary>The client version, encoded as <see cref="ModAbi.Encode"/>.</summary>
        uint ClientVersion { get; }

        /// <summary>Prints a line in the journal and over the world, as a system message.</summary>
        void SystemMessage(string text, ushort hue = 946);

        /// <summary>Sends one complete packet to the server, through the client's normal send
        /// path (encryption, plugins, the packet log).</summary>
        void Send(ReadOnlySpan<byte> packet);

        /// <summary>Writes a line in the client log, prefixed with the mod's id.</summary>
        void Log(string message);
    }
}
