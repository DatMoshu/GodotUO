// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace GUO.Localization;

// Only immutable text crosses the worker boundary. No entities, UI or journal entries.
internal sealed record TranslationRequest(long Id, string Text, string Speaker);
internal sealed record TranslationResult(long Id, string Text, string Status, long Milliseconds);

internal interface IJournalTranslationProvider
{
    Task<string> TranslateAsync(TranslationRequest request, CancellationToken cancellation);
}

internal sealed class JournalTranslationSession : IDisposable
{
    private readonly IJournalTranslationProvider _provider;
    private readonly Channel<TranslationRequest> _requests;
    private readonly ConcurrentQueue<TranslationResult> _results = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly TimeSpan _timeout;
    private readonly int _capacity;
    private readonly Task _worker;
    private int _outstanding;
    private int _disposed;

    public JournalTranslationSession(IJournalTranslationProvider provider, TimeSpan timeout, int capacity = 16)
    {
        if (capacity < 1 || timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException();
        _provider = provider;
        _timeout = timeout;
        _capacity = capacity;
        _requests = Channel.CreateBounded<TranslationRequest>(capacity);
        _worker = Task.Run(Work);
    }

    public Task Completion => _worker;

    public bool TryQueue(TranslationRequest request)
    {
        if (Volatile.Read(ref _disposed) != 0 || string.IsNullOrWhiteSpace(request.Text)
            || request.Text.Length > 1024) return false;
        // Bound pending + in-flight + completed results, even if the game stops pumping.
        if (Interlocked.Increment(ref _outstanding) > _capacity)
        {
            Interlocked.Decrement(ref _outstanding);
            return false;
        }
        if (_requests.Writer.TryWrite(request)) return true;
        Interlocked.Decrement(ref _outstanding);
        return false;
    }

    public bool TryTake(out TranslationResult result)
    {
        if (Volatile.Read(ref _disposed) == 0 && _results.TryDequeue(out result))
        {
            Interlocked.Decrement(ref _outstanding);
            return true;
        }
        result = null;
        return false;
    }

    private async Task Work()
    {
        try
        {
            await foreach (TranslationRequest request in _requests.Reader.ReadAllAsync(_stop.Token))
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                deadline.CancelAfter(_timeout);
                var timer = Stopwatch.StartNew();
                string text = null;
                string status = "translated";
                try
                {
                    text = await _provider.TranslateAsync(request, deadline.Token).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(text) || text.Length > 2048)
                        throw new InvalidOperationException("Invalid translation length.");
                }
                catch (OperationCanceledException) when (!_stop.IsCancellationRequested)
                {
                    status = "timeout";
                }
                catch (Exception) when (!_stop.IsCancellationRequested)
                {
                    // Never log chat, provider response bodies or addresses.
                    status = "unavailable";
                }
                _stop.Token.ThrowIfCancellationRequested();
                _results.Enqueue(new(request.Id, text, status, timer.ElapsedMilliseconds));
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally
        {
            while (_requests.Reader.TryRead(out _)) { }
            if (_provider is IDisposable disposable) disposable.Dispose();
            _stop.Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        _requests.Writer.TryComplete();
        while (_results.TryDequeue(out _)) { }
    }
}
