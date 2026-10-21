"""Build a read-only Fiddler TXT/UOA/CSV contract probe from an explicit pinned source.

The unchanged TXT/UOA/CSV parsing cases and text export methods compile in an isolated
wrapper. Spatial image conversion and all other formats are excluded. Third-party
source and binaries stay in the configured build directory, never this checkout.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
import run as parity

COMMIT = '610c930833dac8d158f08a0af36da43dd90d08b4'
SOURCE_SHA256 = '5204fce496bb316f7d3689a3b296b204e14e4695f42e6423961997c82d2ae767'


def location() -> Path:
    return parity.load_config().build/'fiddler-parity'/'multi-text-reference'


def build(source: Path, out: Path | None = None) -> Path:
    if parity.digest(source) != SOURCE_SHA256:
        raise ValueError('Expected unmodified Fiddler 4.24 MultiComponentList.cs at ' + COMMIT)
    original = source.read_text(encoding='utf-8-sig')
    notice = original[:original.index('using System;')]
    if 'THE BEER-WARE LICENSE' not in notice: raise ValueError('Reference retention notice is missing')
    reader = original.split('case Multis.ImportType.TXT:', 1)[1].split('case Multis.ImportType.UOA:', 1)[0]
    export = 'public void ExportToTextFile' + original.split('public void ExportToTextFile', 1)[1].split('public void ExportToWscFile', 1)[0]
    uoa_reader = original.split('case Multis.ImportType.UOA:', 1)[1].split('case Multis.ImportType.', 1)[0]
    uoa_export = 'public void ExportToUOAFile' + original.split('public void ExportToUOAFile', 1)[1].split('/// <summary>', 1)[0]
    csv_tail = original.split('case Multis.ImportType.CSV:',1)[1].split('            ConvertList();',1)[0]
    csv_reader = csv_tail[:csv_tail.rfind('            }')]
    csv_extra = 'private static int ParseCsvUnk1'+original.split('private static int ParseCsvUnk1',1)[1].split('public void ExportToCsvFile',1)[0]
    csv_export = 'public void ExportToCsvFile'+original.split('public void ExportToCsvFile',1)[1].split('public void ExportToXmlFile',1)[0]
    declaration = original.split('public struct MultiTileEntry', 1)[1].split('/// <summary>', 1)[0]
    entry = 'public struct MultiTileEntry' + declaration
    # Only the surrounding fields/CLI are ours. The reader case and writer method
    # retain the pinned implementation; no parser logic is translated to Python.
    code = notice + '''
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Drawing;
using System.Text.Json;
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
try {
    if (args.Length != 2 || new FileInfo(args[1]).Length > 4 * 1024 * 1024)
        throw new InvalidDataException("Expected read/write and a bounded input file");
    Reference value;
    if (args[0] == "read" || args[0] == "read-uoa" || args[0] == "read-csv") value = new Reference(args[1], args[0]);
    else if (args[0] == "write" || args[0] == "write-uoa" || args[0] == "write-csv") {
        using var json = JsonDocument.Parse(File.ReadAllBytes(args[1]));
        var rows = json.RootElement.GetProperty("records").EnumerateArray().Select(r => new Reference.MultiTileEntry {
            ItemId = r.GetProperty("id").GetUInt16(), OffsetX = r.GetProperty("x").GetInt16(),
            OffsetY = r.GetProperty("y").GetInt16(), OffsetZ = r.GetProperty("z").GetInt16(),
            Flags = r.GetProperty("flags").GetInt32(), Unk1 = r.TryGetProperty("unknown",out var extra) ? extra.GetInt32() : 0 }).ToArray();
        value = new Reference(rows);
        if (args[0] == "write-csv") value.ExportToCsvFile(json.RootElement.GetProperty("output").GetString()!);
        else if (args[0] == "write-uoa") value.ExportToUOAFile(json.RootElement.GetProperty("output").GetString()!);
        else value.ExportToTextFile(json.RootElement.GetProperty("output").GetString()!);
    } else throw new InvalidDataException("Expected read or write");
    Console.WriteLine(JsonSerializer.Serialize(new { records = value.SortedTiles.Select(r => new {
        id = r.ItemId, x = r.OffsetX, y = r.OffsetY, z = r.OffsetZ, flags = r.Flags, unknown = r.Unk1 }) }));
    return 0;
} catch (Exception e) { Console.Error.WriteLine(e.Message); return 2; }
sealed class Reference {
    private Point _min, _max;
    private int MaxHeight;
    public MultiTileEntry[] SortedTiles;
    public Reference(MultiTileEntry[] rows) { SortedTiles = rows; }
    public Reference(string fileName, string format) { _min = _max = Point.Empty; int itemCount; switch (format == "read-csv" ? 2 : format == "read-uoa" ? 1 : 0) { case 0:
''' + reader + '\ncase 1:\n' + uoa_reader + '\ncase 2:\n' + csv_reader + '\n} }\n' + entry + '\n' + export + '\n' + uoa_export + '\n' + csv_extra + '\n' + csv_export + '\n}\n'
    out = (out or location()).resolve(); parity.protect_install(out)
    out.mkdir(parents=True, exist_ok=True)
    (out/'Program.cs').write_text(code, encoding='utf-8')
    (out/'Reference.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup></Project>\n')
    result = subprocess.run(['dotnet', 'build', str(out/'Reference.csproj'), '-c', 'Release', '--nologo'], capture_output=True, text=True, timeout=120)
    (out/'build.log').write_text(result.stdout + result.stderr, encoding='utf-8')
    if result.returncode: raise RuntimeError('Fiddler text reference build failed: ' + (result.stdout + result.stderr)[-12000:])
    binary = out/'bin'/'Release'/'net10.0'/'Reference.dll'
    manifest = {'commit': COMMIT, 'source_sha256': SOURCE_SHA256, 'generated_sha256': hashlib.sha256(code.encode()).hexdigest(),
                'binary_sha256': parity.digest(binary), 'scope': 'unchanged TXT/UOA/CSV cases, CSV extra parser and export methods; no ConvertList or image rendering'}
    (out/'identity.json').write_text(json.dumps(manifest, indent=2)+'\n', encoding='utf-8')
    return binary


def invoke(binary: Path, operation: str, path: Path) -> dict:
    if not binary.is_file(): raise ValueError('Build the Fiddler text probe with python tools/fiddler_parity/multi_text_reference.py --source <pinned-file>')
    identity = json.loads((binary.parents[3]/'identity.json').read_text(encoding='utf-8'))
    if identity.get('commit') != COMMIT or identity.get('source_sha256') != SOURCE_SHA256 or identity.get('binary_sha256') != parity.digest(binary):
        raise ValueError('Fiddler text reference identity changed; rebuild the pinned probe')
    result = subprocess.run(['dotnet', str(binary), operation, str(path)], capture_output=True, text=True, timeout=30)
    if result.returncode: raise ValueError('Pinned Fiddler text contract refused input: ' + result.stderr.strip())
    return json.loads(result.stdout)


def main():
    parser = argparse.ArgumentParser(description=__doc__); parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--out', type=Path)
    args = parser.parse_args()
    try: print(build(args.source, args.out)); return 0
    except (OSError, ValueError, RuntimeError) as error: print(error, file=sys.stderr); return 1


if __name__ == '__main__': raise SystemExit(main())
