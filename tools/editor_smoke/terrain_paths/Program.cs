// SPDX-License-Identifier: BSD-2-Clause
using GUO.Renderer;

if (args.Length != 1) throw new ArgumentException("Pass a new synthetic local fixture directory outside the checkout.");
// Reject network/device fixture spellings before resolving or touching them.
if (args[0].Replace('\\', '/').StartsWith("//", StringComparison.Ordinal))
    throw new ArgumentException("Synthetic fixture must be local.");
string fixture = Path.GetFullPath(args[0]);
string SourceRepo([System.Runtime.CompilerServices.CallerFilePath] string file = "") =>
    Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "../../.."));
string repo = SourceRepo();
StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
if (fixture.Equals(repo, comparison) || fixture.StartsWith(repo + Path.DirectorySeparatorChar, comparison))
    throw new ArgumentException("Synthetic fixture must be outside the checkout.");
if (OperatingSystem.IsWindows() && new DriveInfo(Path.GetPathRoot(fixture)!).DriveType == DriveType.Network)
    throw new ArgumentException("Synthetic fixture must be on a local drive.");
if (Directory.Exists(fixture) || File.Exists(fixture)) throw new IOException("Fixture must be new.");
Directory.CreateDirectory(fixture);
int count = 0;
void Check(bool ok, string name)
{
    if (!ok) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
    count++;
}
string root = Path.Combine(fixture, "layers");
Directory.CreateDirectory(root);
string[] bad = { "a\u0085b", "a\u009fb", "", ".", "..", "../outside.png", "overlays/../../outside.png",
    "overlays\\..\\outside.png", "overlays/..\\outside.png", "/outside.png", "\\outside.png",
    "C:/outside.png", "C:outside.png", "//host/share/image.png", "\\\\host\\share\\image.png",
    "\\\\?\\UNC\\host\\share\\image.png", "\\\\.\\C:\\image.png", "file:///a", "https://example.invalid/a",
    "image.png:stream", "a//b", "a\\\\b", "a/./b", "a/", "a\0b", "a\u001fb", "a\u007fb",
    "a?b", "a*b", "a<b", "a>b", "a|b", "a\"b", "CON", "con.png", "NUL.png", "aux.png", "PRN.txt",
    "COM1.png", "com9.png", "LPT1.png", "COM\u00b9.png", "LPT\u00b2.png", "COM\u00b3.png",
    "con .png", "CONIN$.png", "CONOUT$", "a./b", "a /b", "a/b.", "a/b " };
foreach (string name in bad)
{
    Check(!TerrainLayerPaths.TryNormalize(name, out _, out _), "lexical " + System.Text.Json.JsonSerializer.Serialize(name));
    // An invalid root would throw in canonicalization: lexical reason must win.
    Check(!TerrainLayerPaths.TryResolve("\0", name, out _, out _, out string why)
        && why == "Unsafe layer-relative path.", "lexical-before-root " + System.Text.Json.JsonSerializer.Serialize(name));
}
foreach (string name in new[] { "overlays/a.png", "overlays/literal %ENV% !bang! & name.png", "underlays/space name/\u96EA.png", ".private/a.png", "overlays\\legacy name.png" })
{
    Check(TerrainLayerPaths.TryResolve(root, name, out string path, out string relative, out _)
        && relative == name.Replace('\\', '/') && path.StartsWith(root + Path.DirectorySeparatorChar), "valid " + name);
}
string nested = TerrainLayerPaths.CreateDirectory(root, "overlays/space name/\u96EA");
File.WriteAllText(Path.Combine(nested, "image.png"), "synthetic");
Check(TerrainLayerPaths.TryResolve(root, "overlays\\space name\\\u96EA\\image.png", out _, out string normalized, out _)
    && normalized == "overlays/space name/\u96EA/image.png", "existing legacy normalized");
foreach (string configured in new[] { root, root + Path.DirectorySeparatorChar })
{
    Check(TerrainLayerPaths.IsInside(configured, Path.Combine(nested, "image.png"), out _), "inside root separator " + configured);
    Check(!TerrainLayerPaths.IsInside(configured, root + "-sibling/image.png", out _), "sibling boundary " + configured);
}
string driveRoot = Path.GetPathRoot(root)!;
Check(TerrainLayerPaths.CanonicalRoot(driveRoot) == driveRoot, "filesystem root separator preserved");
Check(TerrainLayerPaths.IsInside(driveRoot, Path.Combine(driveRoot, "synthetic-path-only", "image.png"), out string driveRelative)
    && driveRelative == "synthetic-path-only/image.png", "filesystem root algebra only");
File.WriteAllText(Path.Combine(root, "parent-file"), "synthetic");
Check(!TerrainLayerPaths.TryResolve(root, "parent-file/child.png", out _, out _, out _), "file parent rejected");

// Exact-path API: never send a selected fixture path through a command shell.
bool DirectoryLink(string link, string target)
{
    try { Directory.CreateSymbolicLink(link, target); return true; }
    catch (UnauthorizedAccessException) { Console.WriteLine("UNTESTED directory symlink creation denied"); return false; }
    catch (System.ComponentModel.Win32Exception) { Console.WriteLine("UNTESTED directory symlink creation unsupported"); return false; }
}
string outside = Path.Combine(fixture, "outside");
Directory.CreateDirectory(outside);
File.WriteAllText(Path.Combine(outside, "sentinel.png"), "outside-sentinel");
bool descendantLinked = DirectoryLink(Path.Combine(root, "linked"), outside);
if (descendantLinked)
{
    Check(!TerrainLayerPaths.TryResolve(root, "linked/sentinel.png", out _, out _, out _), "descendant directory link rejected");
    Check(!TerrainLayerPaths.TryResolve(root, "linked/missing/deep.png", out _, out _, out _), "directory link checked before missing child");
    Check(!TerrainLayerPaths.TryResolve(root, "linked", out _, out _, out _), "directory link final leaf rejected");
}
string redirected = Path.Combine(fixture, "trusted-root");
if (DirectoryLink(redirected, root))
{
    Check(TerrainLayerPaths.TryResolve(redirected, "overlays/space name/\u96EA/image.png", out string path, out _, out _)
        && File.ReadAllText(path) == "synthetic", "trusted redirected root works");
    Check(TerrainLayerPaths.TryResolve(Path.Combine(redirected, "overlays"), "space name/\u96EA/image.png", out _, out _, out _),
        "trusted redirected ancestor works");
    if (descendantLinked)
        Check(!TerrainLayerPaths.TryResolve(redirected, "linked/sentinel.png", out _, out _, out _), "descendant through trusted root rejected");
}
foreach (bool dangling in new[] { false, true })
{
    string leaf = Path.Combine(root, dangling ? "dangling.png" : "file-link.png");
    try
    {
        File.CreateSymbolicLink(leaf, Path.Combine(outside, dangling ? "missing.png" : "sentinel.png"));
        Check(!TerrainLayerPaths.TryResolve(root, Path.GetFileName(leaf), out _, out _, out _), "file symlink rejected dangling=" + dangling);
    }
    catch (UnauthorizedAccessException) { Console.WriteLine("UNTESTED file symlink creation denied dangling=" + dangling); }
    catch (System.ComponentModel.Win32Exception) { Console.WriteLine("UNTESTED file symlink creation unsupported dangling=" + dangling); }
}
Check(File.ReadAllText(Path.Combine(outside, "sentinel.png")) == "outside-sentinel", "outside sentinel unchanged");
Console.WriteLine($"PASS {count} assertions; no network spelling passed to candidate I/O. Fixtures retained: {fixture}");
