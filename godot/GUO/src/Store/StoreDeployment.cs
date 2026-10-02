// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.IO;
using System.Text.Json;

namespace GUO.Store;

/// <summary>Atomic deployment selection, independent of installing or executing packs.</summary>
internal static class StoreDeployment
{
    public static void Activate(StoreClient store, string candidate, string active)
    {
        var contentLock = StoreContentLock.Read(candidate);
        Activate(store, contentLock, active);
    }

    public static void Activate(StoreClient store, StoreContentLock contentLock, string active)
    {
        using var contentGuard = store.AcquireContentLock();
        contentLock.VerifySnapshot(store.VerifyContentLocked(contentLock.Pack, contentLock.Version));
        string destination = Path.GetFullPath(active);
        StoreClient.NoLinks(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination));
        string mutex = destination + ".lock";
        StoreClient.NoLinks(mutex);
        using var guard = new FileStream(mutex, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        string stage = destination + ".stage-" + Guid.NewGuid().ToString("N");
        string previous = destination + ".previous";
        StoreClient.NoLinks(previous);
        try
        {
            File.WriteAllBytes(stage, JsonSerializer.SerializeToUtf8Bytes(contentLock, new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(destination)) File.Replace(stage, destination, previous);
            else File.Move(stage, destination);
        }
        finally { if (File.Exists(stage)) File.Delete(stage); }
    }

    public static void Rollback(StoreClient store, string active)
    {
        Activate(store, active + ".previous", active);
    }

    public static void Deactivate(string active)
    {
        string destination = Path.GetFullPath(active), previous = destination + ".previous", mutex = destination + ".lock";
        StoreClient.NoLinks(destination); StoreClient.NoLinks(previous); StoreClient.NoLinks(mutex);
        Directory.CreateDirectory(Path.GetDirectoryName(destination));
        using var guard = new FileStream(mutex, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (File.Exists(destination)) File.Move(destination, previous, true);
    }
}
