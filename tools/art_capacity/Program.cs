// SPDX-License-Identifier: BSD-2-Clause
using System.Buffers.Binary;
using GUO.Labs.ArtCapacity;

internal static class Program
{
    private static int _passed;
    private static readonly ContentProfile Profile = new(0x40000, new byte[32]);

    private static void Check(string name, Action test)
    {
        try { test(); _passed++; }
        catch (Exception e) { throw new Exception(name + ": " + e.Message, e); }
    }
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"expected {expected}, got {actual}");
    }
    private static void Refuse(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new Exception("invalid input accepted");
    }

    // Independent synthetic writer. Explicit layouts, no external files or assets.
    private static byte[] File(RecordKind kind, params uint[] ids)
    {
        int stride = kind == RecordKind.Static9 ? 9 : kind == RecordKind.Multi14 ? 14 : 18;
        var b = new byte[56 + ids.Length * stride];
        "GAC1"u8.CopyTo(b); b[4] = 1; b[6] = (byte)kind;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), 0x40000);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), (uint)ids.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(48), (ulong)(ids.Length * stride));
        for (int i = 0; i < ids.Length; i++)
        {
            var row = b.AsSpan(56 + i * stride, stride);
            BinaryPrimitives.WriteUInt32LittleEndian(row, ids[i]);
            if (kind == RecordKind.Static9)
            {
                row[4] = 7; row[5] = 6; row[6] = 0x80; row[7] = 0x34; row[8] = 0x12;
            }
            else
            {
                BinaryPrimitives.WriteInt16LittleEndian(row[4..], -300);
                BinaryPrimitives.WriteInt16LittleEndian(row[6..], 300);
                BinaryPrimitives.WriteInt16LittleEndian(row[8..], -32768);
                row[10] = 1;
            }
        }
        return b;
    }

    private static byte[] Frame(params Equipment[] entries)
    {
        var b = new byte[12 + entries.Sum(e => e.Hue.HasValue ? 12 : 10)];
        "GAF1"u8.CopyTo(b); BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4), (ushort)b.Length); b[6] = 1;
        int at = 8;
        foreach (var e in entries)
        {
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(at), e.Serial); at += 4;
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(at), e.Art); at += 4;
            b[at++] = e.Layer; b[at++] = (byte)(e.Hue.HasValue ? 1 : 0);
            if (e.Hue.HasValue) { BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(at), e.Hue.Value); at += 2; }
        }
        return b; // zero serial terminates the list; no art-bit overlays
    }

    private static CapacitySession Session()
    {
        var s = new CapacitySession(Profile); s.Accept(true, 1, 1, Profile); return s;
    }

    internal static int Main()
    {
        try { Run(); Console.WriteLine($"[art-capacity] OK: {_passed} synthetic checks; no live protocol enabled."); return 0; }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }

    private static void Run()
    {
        uint[] boundaries = [0, 0x3FFF, 0x4000, 0x7FFF, 0x8000, 65535, 65536, 0x3FFFF];
        foreach (uint id in boundaries)
        {
            Check($"art_index_{id}_preserved", () => Equal(id + 0x4000, Profile.ArtIndex(id)));
            foreach (var kind in new[] { RecordKind.Static9, RecordKind.Multi14, RecordKind.Multi18 })
                Check($"file_{kind}_{id}_preserved", () =>
                {
                    var row = CapacityFiles.Read(File(kind, id), Profile, kind).Single();
                    Equal(id, row.Art);
                    Equal((short)(kind == RecordKind.Static9 ? -128 : -32768), row.Z);
                    Equal((short)(kind == RecordKind.Static9 ? 7 : -300), row.X);
                    Equal((ushort)(kind == RecordKind.Static9 ? 0x1234 : 0), row.Hue);
                });
            Check($"equipment_{id}_preserved", () => Equal(id, Session().ReadEquipment(Frame(new Equipment(0x40000001, id, 1, null))).Single().Art));
        }
        foreach (uint id in new[] { 0x40000u, uint.MaxValue, 0x80000000u })
        {
            Check($"invalid_id_{id}_refused", () => Refuse(() => Profile.ArtIndex(id)));
            Check($"invalid_file_id_{id}_refused", () => Refuse(() => CapacityFiles.Read(File(RecordKind.Static9, id), Profile, RecordKind.Static9)));
            Check($"invalid_wire_id_{id}_refused", () => Refuse(() => Session().ReadEquipment(Frame(new Equipment(1, id, 1, null)))));
        }
        Check("signed_negative_refused", () => Refuse(() => Profile.SignedId(-1)));
        Check("signed_min_refused", () => Refuse(() => Profile.SignedId(int.MinValue)));
        Check("signed_max_refused", () => Refuse(() => Profile.SignedId(int.MaxValue)));
        Check("signed_high_preserved", () => Equal(65536u, Profile.SignedId(65536)));
        foreach (uint slots in new[] { 0u, 31u, 33u, 0x40020u, uint.MaxValue })
            Check($"invalid_capacity_{slots}_refused", () => Refuse(() => new ContentProfile(slots, new byte[32])));
        Check("manifest_length_refused", () => Refuse(() => new ContentProfile(32, new byte[31])));
        Check("manifest_defensively_copied", () =>
        {
            var digest = new byte[32]; var p = new ContentProfile(32, digest); digest[0] = 1;
            Equal((byte)0, p.Manifest[0]);
        });
        foreach (var kind in new[] { RecordKind.Static9, RecordKind.Multi14, RecordKind.Multi18 })
        {
            byte[] good = File(kind, 65535, 65536, 0x3FFFF);
            for (int n = 0; n < good.Length; n++)
            {
                int length = n;
                Check($"truncated_{kind}_{length}_refused", () => Refuse(() => CapacityFiles.Read(good.AsSpan(0, length), Profile, kind)));
            }
            foreach (int at in new[] { 0, 4, 6, 7, 8, 12, 16, 48, 55 })
            {
                byte[] bad = (byte[])good.Clone(); bad[at] ^= 0x80;
                Check($"bad_header_{kind}_{at}_refused", () => Refuse(() => CapacityFiles.Read(bad, Profile, kind)));
            }
            Check($"wrong_explicit_format_{kind}_refused", () => Refuse(() => CapacityFiles.Read(good, Profile, (RecordKind)99)));
            Check($"record_limit_{kind}_accepted", () => Equal(4096, CapacityFiles.Read(File(kind, new uint[4096]), Profile, kind).Length));
            Check($"excess_count_{kind}_refused", () => Refuse(() => CapacityFiles.Read(File(kind, new uint[4097]), Profile, kind)));
        }
        Check("static_wrong_x_refused", () =>
        {
            byte[] bad = File(RecordKind.Static9, 65536); bad[60] = 8;
            Refuse(() => CapacityFiles.Read(bad, Profile, RecordKind.Static9));
        });
        Check("static_wrong_y_refused", () =>
        {
            byte[] bad = File(RecordKind.Static9, 65536); bad[61] = 8;
            Refuse(() => CapacityFiles.Read(bad, Profile, RecordKind.Static9));
        });
        Check("static_positive_z_and_max_hue_preserved", () =>
        {
            byte[] good = File(RecordKind.Static9, 65536);
            good[62] = 127; good[63] = 255; good[64] = 255;
            var row = CapacityFiles.Read(good, Profile, RecordKind.Static9).Single();
            Equal((short)127, row.Z); Equal(ushort.MaxValue, row.Hue);
        });
        foreach (var kind in new[] { RecordKind.Multi14, RecordKind.Multi18 })
            Check($"multi_hidden_{kind}_accepted", () =>
            {
                byte[] good = File(kind, 65536); good[66] = 0;
                Equal(0u, CapacityFiles.Read(good, Profile, kind).Single().Flags);
            });
        Check("multi_unknown_flags_refused", () =>
        {
            byte[] bad = File(RecordKind.Multi14, 65536); bad[66] = 2;
            Refuse(() => CapacityFiles.Read(bad, Profile, RecordKind.Multi14));
        });
        Check("multi18_reserved_refused", () =>
        {
            byte[] bad = File(RecordKind.Multi18, 65536); bad[70] = 1;
            Refuse(() => CapacityFiles.Read(bad, Profile, RecordKind.Multi18));
        });
        Check("ambiguous_126_bytes_uses_explicit_kind", () =>
        {
            var b = File(RecordKind.Multi14, new uint[9]); // 126 divisible by both 9 and 14
            Refuse(() => CapacityFiles.Read(b, Profile, RecordKind.Static9));
        });
        foreach (var slice in new (long Offset, long Length, long Total)[]
        { (-1, 7, 10), (0, -1, 10), (11, 0, 10), (9, 2, 10), (long.MaxValue, 9, 10), (1, long.MaxValue, 10), (0, 0, -1), (0, 0, 67108865) })
            Check($"bad_slice_{slice}_refused", () => Refuse(() => CapacityFiles.Slice(slice.Offset, slice.Length, slice.Total, 1)));
        Check("valid_slice_accepted", () => CapacityFiles.Slice(9, 9, 18, 9));
        Check("partial_record_slice_refused", () => Refuse(() => CapacityFiles.Slice(0, 10, 10, 9)));
        const long land = 512 * (4 + 32 * 30), group = 4 + 32 * 41;
        Check("tiledata_capacity_accepted", () => Equal(0x40000u, CapacityFiles.TileDataRows(land + group * 8192, Profile)));
        foreach (long bytes in new[] { -1, land - 1, land + group - 1, land + group * 8193, long.MaxValue })
            Check($"tiledata_extent_{bytes}_refused", () => Refuse(() => CapacityFiles.TileDataRows(bytes, Profile)));
        Check("art_index_sparse_high_slot_preserved", () =>
        {
            var index = new byte[(0x14001) * 12];
            for (int i = 0; i < index.Length; i += 12) { index[i] = index[i+1] = index[i+2] = index[i+3] = 255; index[i+4] = index[i+5] = index[i+6] = index[i+7] = 255; }
            index.AsSpan(index.Length - 12, 8).Clear(); index[^8] = 4;
            CapacityFiles.ArtIndex(index, 4, Profile);
        });
        foreach (var bad in new[] { new byte[11], new byte[12], new byte[(0x44001) * 12] })
            Check($"bad_art_index_{bad.Length}_refused", () => Refuse(() => CapacityFiles.ArtIndex(bad, 4, Profile)));
        foreach (int offset in new[] { -2, -1, 4, int.MaxValue })
            Check($"bad_art_offset_{offset}_refused", () =>
            {
                var b = new byte[12]; BinaryPrimitives.WriteInt32LittleEndian(b, offset); b[4] = 1;
                Refuse(() => CapacityFiles.ArtIndex(b, 4, Profile));
            });
        NegotiationAndFrames();
        Residency();
        LegacyPacketLengths();
    }

    private static void NegotiationAndFrames()
    {
        foreach (var attempt in new (bool OptIn, ushort Revision, byte Caps, ContentProfile Peer)[]
        { (false,1,1,Profile), (true,0,1,Profile), (true,2,1,Profile), (true,1,0,Profile), (true,1,3,Profile),
          (true,1,1,new(65536,new byte[32])), (true,1,1,new(0x40000,Enumerable.Repeat((byte)1,32).ToArray())), (true,1,1,null) })
            Check($"negotiation_{attempt.OptIn}_{attempt.Revision}_{attempt.Caps}_refused", () =>
            {
                var s = new CapacitySession(Profile);
                Refuse(() => s.Accept(attempt.OptIn, attempt.Revision, attempt.Caps, attempt.Peer));
                Refuse(() => s.ReadEquipment(Frame()));
                Refuse(() => s.Accept(true, 1, 1, Profile)); // terminal failure
            });
        Check("unnegotiated_frame_refused", () => Refuse(() => new CapacitySession(Profile).ReadEquipment(Frame())));
        Check("renegotiation_closes_session", () => { var s = Session(); Refuse(() => s.Accept(true, 1, 1, Profile)); Refuse(() => s.ReadEquipment(Frame())); });
        var good = Frame(new Equipment(0x40000001, 65535, 1, null), new(0x40000002, 65536, 2, 0x8000));
        Check("equipment_hue_flag_separate_from_art", () =>
        {
            var e = Session().ReadEquipment(good); Equal(2, e.Length); Equal((ushort?)0x8000, e[1].Hue); Equal((ushort?)null, e[0].Hue);
        });
        Check("equipment_8000_art_is_not_hue_flag", () => Equal((ushort?)null, Session().ReadEquipment(Frame(new Equipment(1, 0x8000, 1, null))).Single().Hue));
        for (int n = 0; n < good.Length; n++)
        {
            var bad = good[..n];
            Check($"truncated_frame_{n}_refused", () => Refuse(() => Session().ReadEquipment(bad)));
        }
        foreach (int at in new[] { 0, 4, 5, 6, 7, 17 })
        {
            var bad = (byte[])good.Clone(); bad[at] ^= 0x80;
            Check($"bad_frame_field_{at}_refused", () => Refuse(() => Session().ReadEquipment(bad)));
        }
        Check("missing_terminator_refused_with_matching_length", () =>
        {
            var bad = good[..^4]; BinaryPrimitives.WriteUInt16BigEndian(bad.AsSpan(4), (ushort)bad.Length);
            Refuse(() => Session().ReadEquipment(bad));
        });
        Check("early_terminator_with_trailer_refused", () => { var b = (byte[])good.Clone(); b.AsSpan(8,4).Clear(); Refuse(() => Session().ReadEquipment(b)); });
        Check("concatenated_frames_refused", () => Refuse(() => Session().ReadEquipment(good.Concat(Frame()).ToArray())));
        Check("duplicate_layer_refused", () => Refuse(() => Session().ReadEquipment(Frame(new Equipment(1,1,1,null), new(2,65536,1,null)))));
        Check("zero_layer_refused", () => Refuse(() => Session().ReadEquipment(Frame(new Equipment(1,1,0,null)))));
        Check("oversized_frame_refused", () => Refuse(() => Session().ReadEquipment(new byte[513])));
        Check("bad_frame_closes_session", () => { var s = Session(); Refuse(() => s.ReadEquipment(new byte[0])); Refuse(() => s.ReadEquipment(Frame())); });
        Check("all_supported_layers_bounded", () => Equal(31, Session().ReadEquipment(Frame(Enumerable.Range(1,31).Select(i => new Equipment((uint)i,65536,(byte)i,0)).ToArray())).Length));
    }

    private static void Residency()
    {
        Check("cache_high_keys_do_not_alias", () => { var c = new CapacityResidency(Profile); c.Put(0,new byte[1]); c.Put(65536,new byte[2]); Equal(2,c.Count); Equal(3,c.Bytes); });
        Check("cache_entry_eviction_bounded", () => { var c = new CapacityResidency(Profile,2,8); for(uint i=0;i<100;i++) c.Put(i,new byte[2]); Equal(2,c.Count); Equal(false,c.Contains(0)); Equal(true,c.Contains(99)); });
        Check("cache_byte_eviction_bounded", () => { var c = new CapacityResidency(Profile,64,3); c.Put(65535,new byte[2]); c.Put(65536,new byte[2]); Equal(1,c.Count); Equal(2,c.Bytes); });
        Check("cache_replacement_and_invalidation", () => { var c = new CapacityResidency(Profile,2,8); for(int i=0;i<200;i++) c.Put(65536,new byte[2]); c.Put(1,new byte[2]); c.Invalidate(65536); c.Put(2,new byte[2]); Equal(2,c.Count); Equal(4,c.Bytes); });
        Check("cache_reload_invalidates_same_profile", () => { var c = new CapacityResidency(Profile); c.Put(65536,new byte[2]); c.Reload(Profile); Equal(0,c.Count); Equal(0,c.Bytes); });
        Check("cache_manifest_change_drops_residents", () => { var c = new CapacityResidency(Profile); c.Put(65536,new byte[2]); c.Reload(new(32,new byte[32])); Equal(0,c.Count); Refuse(() => c.Contains(65536)); });
        Check("cache_invalid_id_has_no_effect", () => { var c = new CapacityResidency(Profile); c.Put(1,new byte[2]); Refuse(() => c.Put(uint.MaxValue,new byte[1])); Equal(1,c.Count); });
        Check("cache_oversized_image_has_no_effect", () => { var c = new CapacityResidency(Profile); Refuse(() => c.Put(1,new byte[65537])); Equal(0,c.Count); });
        Check("cache_invalid_budget_refused", () => Refuse(() => new CapacityResidency(Profile,int.MaxValue,int.MaxValue)));
    }

    private static void LegacyPacketLengths()
    {
        // Golden framing of every assigned family, with real version calibration.
        // Expanded contracts must never reinterpret these legacy opcodes.
        foreach (var version in new[] { GUO.Utility.ClientVersion.CV_OLD, GUO.Utility.ClientVersion.CV_6017, GUO.Utility.ClientVersion.CV_7090 })
        {
            var table = new GUO.Network.PacketsTable(version);
            foreach (var (id, length) in new (int, short)[]
            {
                (0x25, (short)(version >= GUO.Utility.ClientVersion.CV_6017 ? 21 : 20)),
                (0x3C, -1), (0x2E, 15), (0x78, -1),
                (0xF3, (short)(version >= GUO.Utility.ClientVersion.CV_7090 ? 26 : 24)),
                (0x6C, 19), (0x70, 28), (0xC0, 36), (0xC7, 49), (0x23, 26),
                (0x9E, -1), (0x7C, -1), (0xBF, -1)
            })
                Check($"legacy_{version}_{id:X2}_golden_length", () => Equal(length, table.GetPacketLength(id)));
        }
    }
}
