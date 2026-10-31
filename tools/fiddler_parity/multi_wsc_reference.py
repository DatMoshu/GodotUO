"""Build a scalar WSC probe from the unchanged hash-pinned Fiddler source.

This is a development oracle, not the editor's importer. Generated source and
binaries remain outside the checkout. It deliberately includes Fiddler's
coordinate heuristic and optional DynamicItemIds, but no spatial renderer.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys

import multi_text_reference as shared


def build(source: Path, out: Path) -> Path:
    if shared.parity.digest(source) != shared.SOURCE_SHA256:
        raise ValueError('Expected pinned Fiddler MultiComponentList.cs at '+shared.COMMIT)
    original = source.read_text(encoding='utf-8-sig')
    notice = original[:original.index('using System;')]
    if 'THE BEER-WARE LICENSE' not in notice: raise ValueError('Missing reference notice')
    reader = original.split('case Multis.ImportType.WSC:', 1)[1].split('case Multis.ImportType.CSV:', 1)[0]
    exporter = 'public void ExportToWscFile'+original.split('public void ExportToWscFile', 1)[1].split('public void ExportToUox3File', 1)[0]
    entry = 'public struct MultiTileEntry'+original.split('public struct MultiTileEntry', 1)[1].split('/// <summary>', 1)[0]
    code = notice+'''
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Drawing;
using System.Collections.Generic;
using System.Text.Json;
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
try {
    if (args.Length != 2 || new FileInfo(args[1]).Length > 4 * 1024 * 1024)
        throw new InvalidDataException("Expected read/write and bounded JSON specification");
    using var spec = JsonDocument.Parse(File.ReadAllBytes(args[1]));
    var root = spec.RootElement;
    Reference value;
    if (args[0] == "read") {
        string input = root.GetProperty("input").GetString()!;
        if (new FileInfo(input).Length > 4 * 1024 * 1024) throw new InvalidDataException("WSC exceeds budget");
        Reference.DynamicItemIds = root.TryGetProperty("dynamic_ids", out var dynamicIds)
            ? dynamicIds.EnumerateArray().Select(i => i.GetUInt16()).ToHashSet() : null;
        value = new Reference(input);
    } else if (args[0] == "write") {
        value = new Reference(root.GetProperty("records").EnumerateArray().Select(r => new Reference.MultiTileEntry {
            ItemId = r.GetProperty("id").GetUInt16(), OffsetX = r.GetProperty("x").GetInt16(),
            OffsetY = r.GetProperty("y").GetInt16(), OffsetZ = r.GetProperty("z").GetInt16(),
            Flags = r.GetProperty("flags").GetInt32(), Unk1 = 0 }).ToArray());
        value.ExportToWscFile(root.GetProperty("output").GetString()!);
    } else throw new InvalidDataException("Expected read or write");
    Console.WriteLine(JsonSerializer.Serialize(new { records = value.SortedTiles.Select(r => new {
        id = r.ItemId, x = r.OffsetX, y = r.OffsetY, z = r.OffsetZ, flags = r.Flags, unknown = r.Unk1 }) }));
    return 0;
} catch (Exception e) { Console.Error.WriteLine(e.Message); return 2; }
sealed class Reference {
    private Point _min, _max;
    private int MaxHeight;
    public static HashSet<ushort>? DynamicItemIds { get; set; }
    public MultiTileEntry[] SortedTiles;
    public Reference(MultiTileEntry[] rows) { SortedTiles = rows; }
    public Reference(string fileName) { _min = _max = Point.Empty; int itemCount; switch (0) { case 0:
'''+reader+'\n} }\n'+entry+'\n'+exporter+'\n}\n'
    out = out.resolve(); shared.parity.protect_install(out)
    out.mkdir(parents=True, exist_ok=False)
    (out/'Program.cs').write_text(code, encoding='utf-8')
    (out/'Reference.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup></Project>\n')
    result = subprocess.run(['dotnet', 'build', str(out/'Reference.csproj'), '-c', 'Release', '--nologo'], capture_output=True, text=True, timeout=120)
    (out/'build.log').write_text(result.stdout+result.stderr, encoding='utf-8')
    if result.returncode: raise RuntimeError('WSC reference build failed: '+(result.stdout+result.stderr)[-10000:])
    binary = out/'bin'/'Release'/'net10.0'/'Reference.dll'
    (out/'identity.json').write_text(json.dumps({'commit':shared.COMMIT, 'source_sha256':shared.SOURCE_SHA256,
        'generated_sha256':hashlib.sha256(code.encode()).hexdigest(), 'binary_sha256':shared.parity.digest(binary),
        'scope':'unchanged WSC case and exporter, including coordinate heuristic and DynamicItemIds; no spatial renderer'}, indent=2)+'\n')
    return binary


def invoke(binary: Path, operation: str, spec: Path) -> dict:
    if not binary.is_file():
        raise ValueError('WSC reference missing. Run launchers/dev/fiddler_parity_doctor.bat, then build multi_wsc_reference.py with explicit --source and --out.')
    identity = json.loads((binary.parents[3]/'identity.json').read_text())
    if (identity.get('commit') != shared.COMMIT or identity.get('source_sha256') != shared.SOURCE_SHA256
            or identity.get('binary_sha256') != shared.parity.digest(binary)):
        raise ValueError('WSC reference identity changed; rebuild pinned probe')
    result = subprocess.run(['dotnet', str(binary), operation, str(spec)], capture_output=True, text=True, timeout=30)
    if result.returncode: raise ValueError('Pinned WSC reader refused input: '+result.stderr.strip())
    return json.loads(result.stdout)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, required=True); parser.add_argument('--out', type=Path, required=True)
    args = parser.parse_args()
    try: print(build(args.source, args.out)); return 0
    except (OSError,ValueError,RuntimeError) as error:
        print(str(error)+'\nRun launchers/dev/fiddler_parity_doctor.bat (.sh on Unix) to check the reference prerequisites.',file=sys.stderr)
        return 1


if __name__ == '__main__': raise SystemExit(main())
