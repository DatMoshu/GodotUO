namespace GUO.Host;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using FileAccess = Godot.FileAccess;

/// <summary>
/// A folder the player picked with Android's own folder picker, the Storage
/// Access Framework (G2a, ADR-0021). Godot's native OpenDir dialog returns a
/// tree URI (<c>content://…/tree/…</c>) rather than a path; Godot's FileAccess
/// reads a file in it as <c>treeUri#name</c>, and the grant lasts across
/// restarts. The client's readers are .NET streams and memory maps, which
/// need a real path, so Continue copies the folder's files into the app's own
/// data folder (where <c>tools\android push</c> puts them) and the client
/// reads them there. The picked folder itself is never written.
/// </summary>
/// <remarks>GUO-owned; not in ClassicUO.</remarks>
internal static class SafFolder
{
    private const int Chunk = 4 << 20;

    public static bool IsTree(string path) => path != null && path.StartsWith("content://", StringComparison.Ordinal);

    /// <summary>A file inside the picked tree, in the form Godot's FileAccess takes.</summary>
    public static string Child(string tree, string name) => tree + "#" + name;

    /// <summary>
    /// The files at the top of the tree, by listing it when Godot can; when it
    /// cannot, the required names that open (so a valid folder still passes).
    /// <paramref name="listed"/> says which it was.
    /// </summary>
    public static List<string> Files(string tree, out bool listed)
    {
        // Godot's DirAccess cannot open a tree URI (measured on the Thor:
        // InvalidParameter), and SAF names are case-sensitive while the
        // required list is lower-case (Cliloc.enu, Skills.idx). So the folder
        // is listed the way Android lists one: its content resolver.
        List<string> children = ListChildren(tree);
        if (children != null)
        {
            listed = true;
            return children;
        }

        using DirAccess dir = DirAccess.Open(tree);
        if (dir != null)
        {
            string[] files = dir.GetFiles();
            GD.Print($"[GUO] saf           : listed {files.Length} files in {tree}");
            if (files.Length > 0)
            {
                listed = true;
                return files.ToList();
            }
        }
        else
        {
            GD.Print($"[GUO] saf           : DirAccess cannot open {tree} ({DirAccess.GetOpenError()}); probing the required names");
        }

        listed = false;
        var found = new List<string>();
        foreach (DataRequirements.Entry e in DataRequirements.Required)
        {
            foreach (string name in e.Mul.Concat(e.Uop).Concat(e.Index))
            {
                if (FileAccess.FileExists(Child(tree, name)))
                {
                    found.Add(name);
                }
            }
        }

        return found;
    }

    /// <summary>
    /// The files (not folders) at the top of a tree URI, through
    /// ContentResolver.query on DocumentsContract's children URI, over JNI
    /// (JavaClassWrapper and the AndroidRuntime singleton, as
    /// Platform.Android.SecondDisplay does). Null when that is not possible.
    /// </summary>
    private static List<string> ListChildren(string tree)
    {
        if (!OperatingSystem.IsAndroid() || !Engine.HasSingleton("AndroidRuntime"))
        {
            return null;
        }

        try
        {
            GodotObject activity = Engine.GetSingleton("AndroidRuntime").Call("getActivity").AsGodotObject();
            GodotObject resolver = activity?.Call("getContentResolver").AsGodotObject();
            JavaClass uriClass = JavaClassWrapper.Wrap("android.net.Uri");
            JavaClass documents = JavaClassWrapper.Wrap("android.provider.DocumentsContract");
            GodotObject treeUri = uriClass?.Call("parse", tree).AsGodotObject();
            string treeId = documents?.Call("getTreeDocumentId", treeUri).AsString();
            GodotObject childrenUri = documents?.Call("buildChildDocumentsUriUsingTree", treeUri, treeId).AsGodotObject();
            if (resolver == null || childrenUri == null)
            {
                GD.Print($"[GUO] saf           : no content resolver or children URI for {tree}");
                return null;
            }

            // Typed empty values, not nulls: JavaClassWrapper matches an
            // overload by argument types and matches none with nil (measured:
            // no cursor, no exception). A documents provider ignores the
            // selection and the sort anyway.
            GodotObject cursor = resolver.Call("query", childrenUri, new[] { "_display_name", "mime_type" },
                "", System.Array.Empty<string>(), "").AsGodotObject();
            if (cursor == null)
            {
                GD.Print($"[GUO] saf           : the query for {tree} returned no cursor ({JavaClassWrapper.GetException()})");
                return null;
            }

            var files = new List<string>();
            int folders = 0;
            while (cursor.Call("moveToNext").AsBool())
            {
                string name = cursor.Call("getString", 0).AsString();
                if (cursor.Call("getString", 1).AsString() == "vnd.android.document/directory")
                {
                    folders++;
                }
                else if (!string.IsNullOrEmpty(name))
                {
                    files.Add(name);
                }
            }

            cursor.Call("close");
            GD.Print($"[GUO] saf           : listed {files.Count} files and {folders} folders in {tree}");
            return files;
        }
        catch (Exception ex)
        {
            GD.Print($"[GUO] saf           : listing {tree} failed: {ex.Message}");
            return null;
        }
    }

    // The Windows client's own programs, libraries and logs: never read by
    // GUO. Everything else in the folder is copied, as `tools\android push`
    // copies it, since the client reads more than the required set (.bin,
    // .rle, localized .enu/.deu/... files).
    private static readonly HashSet<string> NotData = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".log", ".pdb", ".msi", ".bat", ".ico", ".url", ".lnk", ".tmp", ".part"
    };

    /// <summary>A file the copy takes: anything but the Windows client's programs, libraries and logs.</summary>
    public static bool IsData(string name) => !NotData.Contains(Path.GetExtension(name));

    /// <summary>As DataSources.Check, over names rather than a folder on disk.</summary>
    public static List<(string Key, string Form)> Check(IEnumerable<string> names)
    {
        var found = new HashSet<string>(names.Select(n => n.ToLowerInvariant()));
        bool Has(string[] candidates) => candidates.Any(n => found.Contains(n.ToLowerInvariant()));
        var result = new List<(string, string)>();
        foreach (DataRequirements.Entry e in DataRequirements.Required)
        {
            string form = Has(e.Uop) ? "uop" : Has(e.Mul) && (e.Index.Length == 0 || Has(e.Index)) ? "mul" : null;
            result.Add((e.Key, form));
        }

        return result;
    }

    /// <summary>
    /// Copies <paramref name="names"/> from the tree into <paramref name="dest"/>,
    /// off the main thread. A file is written under a .part name and renamed
    /// when whole, so a copy cut short never leaves a truncated file that
    /// looks complete. <paramref name="progress"/> gets (bytes done, bytes in
    /// all, current file) on the main thread. Null when done, else why not.
    /// </summary>
    public static Task<string> Copy(string tree, IReadOnlyList<string> names, string dest,
                                     Action<long, long, string> progress, CancellationToken cancel = default)
    {
        return Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(dest);
                long total = 0;
                var sizes = new long[names.Count];
                for (int i = 0; i < names.Count; i++)
                {
                    using FileAccess f = FileAccess.Open(Child(tree, names[i]), FileAccess.ModeFlags.Read);
                    sizes[i] = f?.GetLength() > 0 ? (long)f.GetLength() : 0;
                    total += sizes[i];
                }

                long done = 0;
                var buffer = new byte[Chunk];
                for (int i = 0; i < names.Count; i++)
                {
                    cancel.ThrowIfCancellationRequested();
                    string name = names[i];
                    using FileAccess src = FileAccess.Open(Child(tree, name), FileAccess.ModeFlags.Read);
                    if (src == null)
                    {
                        return $"could not read {name} ({FileAccess.GetOpenError()})";
                    }

                    string target = Path.Combine(dest, name);
                    string part = target + ".part";
                    using (var dst = new FileStream(part, FileMode.Create, System.IO.FileAccess.Write, FileShare.None, Chunk))
                    {
                        long left = sizes[i];
                        while (left > 0)
                        {
                            cancel.ThrowIfCancellationRequested();
                            byte[] got = src.GetBuffer(Math.Min(left, Chunk));
                            if (got.Length == 0)
                            {
                                return $"{name} ended early ({FileAccess.GetOpenError()})";
                            }

                            dst.Write(got, 0, got.Length);
                            left -= got.Length;
                            done += got.Length;
                            Callable.From(() => progress?.Invoke(done, total, name)).CallDeferred();
                        }
                    }

                    File.Move(part, target, overwrite: true);
                }

                GD.Print($"[GUO] saf           : copied {names.Count} files, {total / (1 << 20)} MiB, into {dest}");
                return null;
            }
            catch (OperationCanceledException)
            {
                return "stopped";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ex.Message;
            }
        }, cancel);
    }

    /// <summary>
    /// Gives back the app's lasting access to picked folders: the one given,
    /// or every one when <paramref name="tree"/> is null (--saf-forget). After
    /// the copy GUO never reads the picked folder again, so it keeps no grant.
    /// Returns how many were released.
    /// </summary>
    public static int Release(string tree = null)
    {
        if (!OperatingSystem.IsAndroid() || !Engine.HasSingleton("AndroidRuntime"))
        {
            return 0;
        }

        try
        {
            GodotObject activity = Engine.GetSingleton("AndroidRuntime").Call("getActivity").AsGodotObject();
            GodotObject resolver = activity?.Call("getContentResolver").AsGodotObject();
            GodotObject list = resolver?.Call("getPersistedUriPermissions").AsGodotObject();
            if (list == null)
            {
                return 0;
            }

            int released = 0;
            int count = list.Call("size").AsInt32();
            for (int i = 0; i < count; i++)
            {
                GodotObject permission = list.Call("get", i).AsGodotObject();
                GodotObject uri = permission.Call("getUri").AsGodotObject();
                string text = uri.Call("toString").AsString();
                if (tree != null && text != tree)
                {
                    continue;
                }

                // Read and write, as Godot's dialog takes them.
                int flags = (permission.Call("isReadPermission").AsBool() ? 1 : 0) | (permission.Call("isWritePermission").AsBool() ? 2 : 0);
                resolver.Call("releasePersistableUriPermission", uri, flags);
                GD.Print($"[GUO] saf           : released the grant for {text}");
                released++;
            }

            return released;
        }
        catch (Exception ex)
        {
            GD.Print($"[GUO] saf           : releasing grants failed: {ex.Message}");
            return 0;
        }
    }

    /// <summary>The picked folder as the player would name it, from the tree URI's document id.</summary>
    public static string Label(string tree)
    {
        int at = tree.LastIndexOf("/tree/", StringComparison.Ordinal);
        string id = at < 0 ? tree : Uri.UnescapeDataString(tree[(at + 6)..]);
        return id.StartsWith("primary:", StringComparison.Ordinal) ? "Internal storage/" + id[8..] : id;
    }
}
