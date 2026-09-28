// GUO addition, not a port: upstream ClassicUO checks no server before it connects.

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace GUO.Input.Touch.Pregame;

/// <summary>
/// Live status for the Servers tab (docs/ui/second_screen_pregame.md): a bare
/// TCP connect to a server's address, timed from when the name has resolved.
/// No login and no packets. At most <see cref="AtOnce"/> run together, each
/// gives up after <see cref="TimeoutMs"/>, and a result is asked for again
/// only once it is <see cref="EveryMs"/> old.
/// </summary>
internal static class ServerPing
{
    public const int AtOnce = 8;
    public const int TimeoutMs = 3000;
    public const int EveryMs = 60000;

    public enum Kind { Unknown, Up, Down }

    /// <summary>The last timing; <paramref name="Busy"/> while a new one runs (the old one still shows).</summary>
    public readonly record struct Result(Kind Kind, int Ms, long At, bool Busy);

    private static readonly ConcurrentDictionary<string, Result> _results = new();
    private static readonly SemaphoreSlim _gate = new(AtOnce);
    private static int _running;

    /// <summary>How many connects are open now (the probe checks the limit).</summary>
    public static int Running => _running;

    /// <summary>The most that were ever open together.</summary>
    public static int MostAtOnce { get; private set; }

    private static string Key(ServerEntry e) => $"{e.Host?.Trim().ToLowerInvariant()}:{e.Port}";

    public static Result Get(ServerEntry e) => _results.TryGetValue(Key(e), out Result r) ? r : default;

    /// <summary>
    /// Times <paramref name="e"/> unless a timing is running or one is fresh
    /// (<paramref name="now"/> forces a new one, as Refresh does).
    /// </summary>
    public static void Want(ServerEntry e, bool now = false)
    {
        if (e == null || string.IsNullOrWhiteSpace(e.Host))
        {
            return;
        }

        string key = Key(e);
        Result r = Get(e);

        if (r.Busy || (!now && r.Kind != Kind.Unknown && Environment.TickCount64 - r.At < EveryMs))
        {
            return;
        }

        _results[key] = r with { Busy = true };
        string host = e.Host.Trim();
        int port = e.Port;
        _ = Task.Run(() => Time(key, host, port));
    }

    private static async Task Time(string key, string host, int port)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        int running = Interlocked.Increment(ref _running);
        MostAtOnce = Math.Max(MostAtOnce, running);

        try
        {
            using var cts = new CancellationTokenSource(TimeoutMs);
            IPAddress[] addresses = IPAddress.TryParse(host, out IPAddress ip)
                ? new[] { ip }
                : await Dns.GetHostAddressesAsync(host, cts.Token).ConfigureAwait(false);
            using var tcp = new TcpClient(addresses[0].AddressFamily);
            var clock = Stopwatch.StartNew();
            await tcp.ConnectAsync(addresses[0], port, cts.Token).ConfigureAwait(false);
            _results[key] = new Result(Kind.Up, Math.Max(1, (int) clock.ElapsedMilliseconds), Environment.TickCount64, false);
        }
        catch (Exception)
        {
            _results[key] = new Result(Kind.Down, 0, Environment.TickCount64, false);
        }
        finally
        {
            Interlocked.Decrement(ref _running);
            _gate.Release();
        }
    }

    /// <summary>For the probe: forget every timing.</summary>
    public static void Clear()
    {
        _results.Clear();
        MostAtOnce = 0;
    }
}
