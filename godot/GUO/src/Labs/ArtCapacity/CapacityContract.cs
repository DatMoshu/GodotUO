// SPDX-License-Identifier: BSD-2-Clause
#if GUO_LABS
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace GUO.Labs.ArtCapacity;

// Offline candidate contract only. Never registered with the game packet table.
internal sealed class ContentProfile
{
    internal const uint LandSlots = 0x4000, MaxStaticSlots = 0x40000;
    internal uint StaticSlots { get; }
    private readonly byte[] _manifest;
    internal ReadOnlySpan<byte> Manifest => _manifest;

    internal ContentProfile(uint slots, ReadOnlySpan<byte> manifest)
    {
        if (slots == 0 || slots > MaxStaticSlots || slots % 32 != 0 || manifest.Length != 32)
            throw new InvalidDataException("Invalid capacity or manifest.");
        StaticSlots = slots;
        _manifest = manifest.ToArray();
    }

    internal uint ArtIndex(uint id)
    {
        if (id >= StaticSlots) throw new InvalidDataException("Static ID outside profile.");
        return checked(id + LandSlots);
    }

    internal uint SignedId(int id)
    {
        if (id < 0) throw new InvalidDataException("Negative static ID.");
        ArtIndex((uint)id);
        return (uint)id;
    }

    internal bool Matches(ContentProfile other) => other != null && StaticSlots == other.StaticSlots
        && Manifest.SequenceEqual(other.Manifest);
}

internal enum RecordKind : byte { Static9 = 1, Multi14 = 2, Multi18 = 3 }
internal readonly record struct Component(uint Art, short X, short Y, short Z, uint Flags, ushort Hue);

internal static class CapacityFiles
{
    internal const int HeaderBytes = 56, MaxRecords = 4096;
    internal const long MaxFileBytes = 64 * 1024 * 1024;

    internal static int Stride(RecordKind kind) => kind switch
    {
        RecordKind.Static9 => 9, RecordKind.Multi14 => 14, RecordKind.Multi18 => 18,
        _ => throw new InvalidDataException("Unknown record format.")
    };

    internal static void Slice(long offset, long length, long total, int stride)
    {
        if (stride <= 0 || total < 0 || total > MaxFileBytes || offset < 0 || length < 0
            || offset > total || length > total - offset || length % stride != 0)
            throw new InvalidDataException("Invalid bounded file slice.");
    }

    internal static Component[] Read(ReadOnlySpan<byte> bytes, ContentProfile profile, RecordKind expected)
    {
        if (bytes.Length < HeaderBytes || !bytes[..4].SequenceEqual("GAC1"u8)
            || BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]) != 1 || bytes[7] != 0)
            throw new InvalidDataException("Unsupported content header.");
        var kind = (RecordKind)bytes[6];
        var stride = Stride(kind);
        if (kind != expected || BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) != profile.StaticSlots
            || !bytes.Slice(16, 32).SequenceEqual(profile.Manifest))
            throw new InvalidDataException("Content profile mismatch.");
        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        var length = BinaryPrimitives.ReadUInt64LittleEndian(bytes[48..]);
        if (count > MaxRecords || length != (ulong)count * (uint)stride
            || length != (ulong)(bytes.Length - HeaderBytes))
            throw new InvalidDataException("Invalid content count or extent.");
        // Validate all records before allocating the output or publishing any entry.
        for (var i = 0; i < count; i++) ReadComponent(bytes.Slice(HeaderBytes + i * stride, stride), kind, profile);
        var result = new Component[count];
        for (var i = 0; i < count; i++)
            result[i] = ReadComponent(bytes.Slice(HeaderBytes + i * stride, stride), kind, profile);
        return result;
    }

    private static Component ReadComponent(ReadOnlySpan<byte> b, RecordKind kind, ContentProfile p)
    {
        uint id = BinaryPrimitives.ReadUInt32LittleEndian(b);
        p.ArtIndex(id);
        if (kind == RecordKind.Static9)
        {
            if (b[4] > 7 || b[5] > 7) throw new InvalidDataException("Static coordinate outside block.");
            return new(id, b[4], b[5], unchecked((sbyte)b[6]), 0, BinaryPrimitives.ReadUInt16LittleEndian(b[7..]));
        }
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(b[10..]);
        if (flags > 1 || (kind == RecordKind.Multi18 && BinaryPrimitives.ReadUInt32LittleEndian(b[14..]) != 0))
            throw new InvalidDataException("Unsupported multi flags or reserved data.");
        return new(id, BinaryPrimitives.ReadInt16LittleEndian(b[4..]), BinaryPrimitives.ReadInt16LittleEndian(b[6..]),
            BinaryPrimitives.ReadInt16LittleEndian(b[8..]), flags, 0);
    }

    // The High Seas row sizes are positional metadata, not widened IDs.
    internal static uint TileDataRows(long fileBytes, ContentProfile profile)
    {
        const long landBytes = 512 * (4 + 32 * 30), groupBytes = 4 + 32 * 41;
        var tail = fileBytes - landBytes;
        if (fileBytes < landBytes || fileBytes > MaxFileBytes || tail % groupBytes != 0
            || tail / groupBytes > profile.StaticSlots / 32)
            throw new InvalidDataException("Invalid High Seas tiledata extent.");
        return checked((uint)(tail / groupBytes * 32));
    }

    internal static void ArtIndex(ReadOnlySpan<byte> index, long artBytes, ContentProfile profile)
    {
        if (index.Length % 12 != 0 || index.Length / 12 > profile.StaticSlots + ContentProfile.LandSlots)
            throw new InvalidDataException("Invalid art index extent.");
        Slice(0, 0, artBytes, 1);
        for (int i = 0; i < index.Length; i += 12)
        {
            int offset = BinaryPrimitives.ReadInt32LittleEndian(index[i..]);
            int length = BinaryPrimitives.ReadInt32LittleEndian(index[(i + 4)..]);
            if (offset == -1 && length == -1) continue; // sparse hole, never compact
            if (length == 0) throw new InvalidDataException("Noncanonical empty art slot.");
            Slice(offset, length, artBytes, 1);
        }
    }
}

internal readonly record struct Equipment(uint Serial, uint Art, byte Layer, ushort? Hue);

internal sealed class CapacitySession
{
    // Local simulation of a matched offer/ack; no network opcode is assigned.
    private readonly ContentProfile _profile;
    private bool _active, _closed;
    internal const int MaxFrameBytes = 512, MaxEquipment = 32;

    internal CapacitySession(ContentProfile profile) { _profile = profile; }

    internal void Accept(bool localOptIn, ushort revision, byte capabilities, ContentProfile peer)
    {
        if (_closed || _active || !localOptIn || revision != 1 || capabilities != 1 || !_profile.Matches(peer))
        {
            _active = false;
            _closed = true;
            throw new InvalidDataException("Art-capacity negotiation refused.");
        }
        _active = true;
    }

    internal Equipment[] ReadEquipment(ReadOnlySpan<byte> frame)
    {
        if (!_active || _closed) throw new InvalidDataException("No active art-capacity session.");
        try
        {
            if (frame.Length < 12 || frame.Length > MaxFrameBytes || !frame[..4].SequenceEqual("GAF1"u8)
                || BinaryPrimitives.ReadUInt16BigEndian(frame[4..]) != frame.Length || frame[6] != 1 || frame[7] != 0)
                throw new InvalidDataException("Invalid lab frame.");
            int count = Walk(frame, Span<Equipment>.Empty);
            var result = new Equipment[count];
            Walk(frame, result);
            return result;
        }
        catch (InvalidDataException) { _active = false; _closed = true; throw; }
    }

    private int Walk(ReadOnlySpan<byte> frame, Span<Equipment> output)
    {
        int at = 8, count = 0;
        Span<bool> layers = stackalloc bool[32];
        layers.Clear();
        while (at + 4 <= frame.Length)
        {
            uint serial = BinaryPrimitives.ReadUInt32BigEndian(frame[at..]);
            at += 4;
            if (serial == 0)
            {
                if (at != frame.Length) throw new InvalidDataException("Trailing equipment bytes.");
                return count;
            }
            if (count == MaxEquipment || at + 6 > frame.Length) throw new InvalidDataException("Equipment bound exceeded.");
            uint art = BinaryPrimitives.ReadUInt32BigEndian(frame[at..]); at += 4;
            _profile.ArtIndex(art);
            byte layer = frame[at++], flags = frame[at++];
            if (layer == 0 || layer >= layers.Length || layers[layer] || (flags & ~1) != 0)
                throw new InvalidDataException("Invalid equipment layer or flags.");
            layers[layer] = true;
            ushort? hue = null;
            if ((flags & 1) != 0)
            {
                if (at + 2 > frame.Length) throw new InvalidDataException("Missing equipment hue.");
                hue = BinaryPrimitives.ReadUInt16BigEndian(frame[at..]); at += 2;
            }
            if (!output.IsEmpty) output[count] = new(serial, art, layer, hue);
            count++;
        }
        throw new InvalidDataException("Missing equipment terminator.");
    }
}

// Single-threaded synthetic residency model, not the production GPU cache.
internal sealed class CapacityResidency
{
    private ContentProfile _profile;
    private readonly int _maxEntries, _maxBytes;
    private readonly Dictionary<uint, byte[]> _entries = new();
    private readonly Queue<uint> _fifo = new();
    internal int Bytes { get; private set; }
    internal int Count => _entries.Count;

    internal CapacityResidency(ContentProfile profile, int entries = 64, int bytes = 1024 * 1024)
    {
        if (entries < 1 || entries > 64 || bytes < 1 || bytes > 1024 * 1024) throw new InvalidDataException("Invalid residency budget.");
        _profile = profile; _maxEntries = entries; _maxBytes = bytes;
    }

    internal void Reload(ContentProfile profile)
    {
        // Explicit reload invalidates even when the manifest stays the same.
        _entries.Clear(); _fifo.Clear(); Bytes = 0; _profile = profile;
    }

    internal void Put(uint id, ReadOnlySpan<byte> pixels)
    {
        _profile.ArtIndex(id);
        if (pixels.Length > 65536 || pixels.Length > _maxBytes) throw new InvalidDataException("Image exceeds residency budget.");
        // Replacement clears ordering too: no stale FIFO nodes can accumulate.
        if (_entries.ContainsKey(id)) Invalidate(id);
        while (_entries.Count >= _maxEntries || pixels.Length > _maxBytes - Bytes)
        {
            uint victim = _fifo.Dequeue(); Bytes -= _entries[victim].Length; _entries.Remove(victim);
        }
        _entries.Add(id, pixels.ToArray()); _fifo.Enqueue(id); Bytes += pixels.Length;
    }

    internal bool Contains(uint id) { _profile.ArtIndex(id); return _entries.ContainsKey(id); }

    internal void Invalidate(uint id)
    {
        _profile.ArtIndex(id);
        if (!_entries.Remove(id, out var old)) return;
        Bytes -= old.Length;
        int n = _fifo.Count;
        while (n-- > 0) { uint key = _fifo.Dequeue(); if (key != id) _fifo.Enqueue(key); }
    }
}
#endif
