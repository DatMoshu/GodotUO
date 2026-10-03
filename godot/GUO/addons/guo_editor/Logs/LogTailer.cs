#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

/// <summary>One thing the tailer found: a line of the file, or a notice of its own ("file truncated").</summary>
public readonly record struct TailItem(bool Notice, string Text);

/// <summary>
/// Follows one log file from a worker task, by polling its size. It opens the file only for the length
/// of a read (shared with the writer: a server holds its log open on Windows), never blocks the main
/// thread, copes with a file that does not exist yet, one that is truncated or replaced (rotation), and
/// reads only the end of a huge one. Nothing here is Godot: the main thread drains <see cref="Queue"/>.
/// </summary>
/// <remarks>
/// It holds no file handle between polls, so what an assembly reload must release is the task:
/// <see cref="Dispose"/> cancels it and waits briefly. <see cref="Live"/> counts tailers not yet disposed.
/// </remarks>
public sealed class LogTailer : IDisposable
{
    private static int _live;
    private const int ChunkBytes = 1024 * 1024;
    private const int MaxCarry = 64 * 1024;

    private readonly Func<string> _path;
    private readonly CancellationTokenSource _cts = new();
    private Task _task;
    private int _disposed;

    /// <summary>Tailers started and not yet disposed.</summary>
    public static int Live => Volatile.Read(ref _live);

    public ConcurrentQueue<TailItem> Queue { get; } = new();

    /// <summary>Items kept while nobody drains the queue (a paused view); older ones are dropped.</summary>
    public int MaxQueued { get; set; } = 100_000;

    /// <summary>How long between looks at the file.</summary>
    public int PollMilliseconds { get; set; } = 250;

    /// <summary>A file larger than this is entered at its end: only the last this-many bytes are read first.</summary>
    public int InitialTailBytes { get; set; } = 512 * 1024;

    /// <summary>Waiting for the file / following / cannot read: ...</summary>
    public string State { get; private set; } = "starting";

    /// <summary>The file being followed now (a folder source picks the newest file), or null.</summary>
    public string CurrentPath { get; private set; }

    public bool Running => _task != null && !_task.IsCompleted;

    public LogTailer(Func<string> path)
    {
        _path = path;
    }

    public LogTailer(string path) : this(() => path)
    {
    }

    public void Start()
    {
        if (_task != null)
        {
            return;
        }

        Interlocked.Increment(ref _live);
        _task = Task.Run(() => Loop(_cts.Token));
    }

    private void Put(bool notice, string text)
    {
        Queue.Enqueue(new TailItem(notice, text));
        while (Queue.Count > MaxQueued && Queue.TryDequeue(out _))
        {
        }
    }

    private async Task Loop(CancellationToken ct)
    {
        string last = null;
        long offset = 0;
        DateTime created = default;
        byte[] carry = Array.Empty<byte>();
        bool first = true;
        bool skipPartial = false;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                string p = _path();
                if (p != last)
                {
                    if (last != null && p != null)
                    {
                        Put(true, $"--- now following {Path.GetFileName(p)} ---");
                    }

                    last = p;
                    CurrentPath = p;
                    offset = 0;
                    carry = Array.Empty<byte>();
                    first = true;
                }

                if (p == null || !File.Exists(p))
                {
                    if (!first && offset > 0)
                    {
                        Put(true, "--- the file is gone; waiting for it ---");
                    }

                    State = "waiting for the file";
                    offset = 0;
                    carry = Array.Empty<byte>();
                    first = true;
                }
                else
                {
                    var info = new FileInfo(p);
                    long length = info.Length;
                    DateTime made = info.CreationTimeUtc;
                    if (first)
                    {
                        created = made;
                        if (length > InitialTailBytes)
                        {
                            offset = length - InitialTailBytes;
                            skipPartial = true;
                            Put(true, $"--- {length / 1024} KB file: showing its last {InitialTailBytes / 1024} KB ---");
                        }

                        first = false;
                    }
                    else if (length < offset || made != created)
                    {
                        Put(true, "--- the file was truncated or replaced; following from its start ---");
                        offset = 0;
                        carry = Array.Empty<byte>();
                        skipPartial = false;
                        created = made;
                    }

                    State = "following";
                    for (int round = 0; round < 8 && length > offset && !ct.IsCancellationRequested; round++)
                    {
                        (offset, carry, skipPartial) = ReadChunk(p, offset, carry, skipPartial);
                        length = new FileInfo(p).Length;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                State = "cannot read: " + ex.Message;
            }

            try
            {
                await Task.Delay(Math.Max(10, PollMilliseconds), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private (long, byte[], bool) ReadChunk(string path, long offset, byte[] carry, bool skipPartial)
    {
        byte[] buffer;
        int read;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan))
        {
            if (offset > fs.Length)
            {
                return (0, Array.Empty<byte>(), false);
            }

            fs.Seek(offset, SeekOrigin.Begin);
            buffer = new byte[(int)Math.Min(ChunkBytes, Math.Max(0, fs.Length - offset))];
            read = fs.Read(buffer, 0, buffer.Length);
        }

        if (read <= 0)
        {
            return (offset, carry, skipPartial);
        }

        long fresh = offset + read;
        byte[] data = carry.Length == 0 ? buffer.AsSpan(0, read).ToArray() : carry.Concat(buffer.Take(read)).ToArray();
        int start = 0;
        if (offset == 0 && carry.Length == 0 && data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
        {
            start = 3;
        }

        int end = Array.LastIndexOf(data, (byte)'\n');
        if (end < 0)
        {
            // No newline yet: a line still being written. Keep it, unless it has grown absurd.
            if (data.Length > MaxCarry)
            {
                Emit(Encoding.UTF8.GetString(data, start, data.Length - start), ref skipPartial);
                return (fresh, Array.Empty<byte>(), skipPartial);
            }

            return (fresh, data, skipPartial);
        }

        string text = Encoding.UTF8.GetString(data, start, end + 1 - start);
        foreach (string line in text.Split('\n').SkipLast(1))
        {
            Emit(line, ref skipPartial);
        }

        return (fresh, data.AsSpan(end + 1).ToArray(), skipPartial);
    }

    private void Emit(string line, ref bool skipPartial)
    {
        if (skipPartial)
        {
            // The first line after jumping into the middle of a big file is a stump.
            skipPartial = false;
            return;
        }

        Put(false, line.TrimEnd('\r'));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cts.Cancel();
        if (_task != null)
        {
            try
            {
                _task.Wait(2000);
            }
            catch (AggregateException)
            {
            }

            Interlocked.Decrement(ref _live);
        }

        _cts.Dispose();
    }
}
#endif
