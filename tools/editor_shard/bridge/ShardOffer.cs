// SPDX-License-Identifier: BSD-2-Clause
// A shard's pack offer to GUO clients, guo/shard-offer@1 (docs/data_formats.md section 12, ADR-0026 section 4a).
// The shard speaks first: ModernUO accepts no 0xBF from a client before it has a character, so the client never asks.
// No file, no offer: a shard without Data/GUO/shard-offer.json sends nothing and behaves as before.
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using Server;
using Server.Logging;
using Server.Network;

namespace GUO.EditorBridge;

public static class ShardOffer
{
    private static readonly ILogger Log = LogFactory.GetLogger(typeof(ShardOffer));

    public const ushort Subcommand = 0x4755;
    public const byte Format = 1;
    public const int MaxJson = 16384;

    private static byte[] _packet;

    /// <summary>The offer's packet, or why there is none. UO_SHARD_OFFER names a file for this run; otherwise
    /// Data/GUO/shard-offer.json, where tools/shard_content/run.py offer writes it.</summary>
    public static byte[] Build(string path, out string why)
    {
        why = null;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) { why = "no offer file"; return null; }
        byte[] json;
        try
        {
            if (new FileInfo(path).Length > MaxJson) { why = $"offer is over {MaxJson} bytes"; return null; }
            json = File.ReadAllBytes(path);
            if (json.Length > MaxJson) { why = $"offer is over {MaxJson} bytes"; return null; }
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("schema", out var schema) || schema.GetString() != "guo/shard-offer@1")
            {
                why = "not a guo/shard-offer@1 offer";
                return null;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException or UnauthorizedAccessException)
        { why = "unreadable offer: " + ex.Message; return null; }

        // 0xBF, its length, the subcommand, the format byte, the JSON.
        int length = 6 + json.Length;
        var packet = new byte[length];
        packet[0] = 0xBF;
        packet[1] = (byte)(length >> 8);
        packet[2] = (byte)length;
        packet[3] = Subcommand >> 8;
        packet[4] = Subcommand & 0xFF;
        packet[5] = Format;
        json.CopyTo(packet, 6);
        return packet;
    }

    public static void Initialize()
    {
        string path = Environment.GetEnvironmentVariable("UO_SHARD_OFFER");
        if (string.IsNullOrWhiteSpace(path)) path = Path.Combine(Core.BaseDirectory, "Data", "GUO", "shard-offer.json");
        _packet = Build(path, out string why);
        if (_packet == null)
        {
            if (File.Exists(path)) Log.Warning("GUO shard offer: not sent ({0})", why);
            return;
        }
        Log.Information("GUO shard offer: {0} bytes, sent to every client at login", _packet.Length);
        EventSink.Connected += OnConnected;
    }

    private static void OnConnected(Mobile m)
    {
        NetState connected = m?.NetState;
        // Connected fires before the login packets; the offer goes after them, as UltimaLive's does.
        Timer.DelayCall(TimeSpan.FromSeconds(2), () =>
        {
            NetState ns = m?.NetState;
            if (ns != null && ns == connected && _packet != null) ns.Send(_packet);
        });
    }
}
