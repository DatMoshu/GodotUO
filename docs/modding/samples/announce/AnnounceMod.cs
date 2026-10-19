// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Buffers.Binary;
using System.Text;
using GUO.ModApi;

namespace GuoMods.Announce
{
    /// <summary>Packet 0xF9, server to client, dynamic length:
    /// byte 0xF9, ushort length (big-endian, whole packet), ushort hue, then UTF-8 text.</summary>
    public sealed class AnnounceMod : IGuoMod
    {
        public const byte PacketId = 0xF9;

        public void Register(IModRegistrar registrar)
        {
            registrar.AddPacket(new PacketRegistration
            {
                Id = PacketId,
                Length = -1,
                Handler = OnAnnounce
            });
        }

        private static void OnAnnounce(IModContext context, ReadOnlySpan<byte> packet)
        {
            if (packet.Length < 5)
                throw new FormatException("announce packet is shorter than its header");

            ushort hue = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(3, 2));
            string text = Encoding.UTF8.GetString(packet.Slice(5));
            context.SystemMessage(text, hue);
        }

        /// <summary>The bytes a shard sends; also what the tests feed the client.</summary>
        public static byte[] Build(string text, ushort hue)
        {
            byte[] body = Encoding.UTF8.GetBytes(text);
            byte[] packet = new byte[5 + body.Length];
            packet[0] = PacketId;
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(1, 2), (ushort)packet.Length);
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(3, 2), hue);
            body.CopyTo(packet, 5);
            return packet;
        }
    }
}
