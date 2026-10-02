// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using GUO.Game.Scripting;

namespace GUO.Store;

/// <summary>A review of verified bytes, not permission to execute. Immutable to callers.</summary>
internal sealed class ScriptPackReview
{
    internal readonly StoreVerifiedContent Content;
    internal readonly string Source;
    internal readonly string[] Grants;
    public string PackId { get; }
    public string Version { get; }
    public string ComponentId { get; }
    public string Identity => PackId + ":" + ComponentId;
    public string ApprovalIdentity => Content.IdentityHash + ":" + Identity;
    public IReadOnlyList<string> Capabilities => Array.AsReadOnly(Grants);
    internal ScriptPackReview(StoreVerifiedContent content, string packId, string componentId,
        string version, string source, IEnumerable<string> capabilities)
    {
        Content = content; PackId = packId; ComponentId = componentId; Version = version;
        Source = source; Grants = capabilities.ToArray();
    }
}

/// <summary>Game-thread managed script lifecycle. Review/install are inert; approval, enable
/// and Run are separate calls. Approval is session-only and binds the entire dependency closure.
/// Store owners must route managed removals through Uninstall and call Reconcile after changes.</summary>
internal sealed class ScriptPackSession : IDisposable
{
    private readonly StoreClient _store;
    private readonly IScriptHost _host;
    private readonly Dictionary<string, ScriptPackReview> _approvals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ScriptPackReview> _enabled = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ScriptPackReview> _previous = new(StringComparer.Ordinal);
    private ScriptRunner _runner;
    private ScriptPackReview _running;
    private int _lease;
    private int _dirty;
    private bool _disposed;
    public bool Running => _runner?.Running == true;
    public string Status => _runner?.Status ?? "Ready";

    public ScriptPackSession(StoreClient store, IScriptHost host)
    {
        _store = store; _host = host;
        StoreClient.PackChanged += OnPackChanged;
    }
    private void OnPackChanged(string operation, string id, string version) => Interlocked.Exchange(ref _dirty, 1);

    public ScriptPackReview Review(string packId, string version, string componentId)
    {
        var closure = _store.VerifyContent(packId, version);
        var pack = closure.Packs[packId];
        var manifest = pack.Manifest;
        StorePack.Require(manifest.Schema == "guo/store-pack@2", "Managed execution requires a v2 component");
        var component = manifest.Components.SingleOrDefault(c => c.Id == componentId);
        StorePack.Require(component != null && component.Type == "script" && component.Target == "client",
            "Only client script components can execute here");
        StorePack.Require(component.Language == "razor-ce" && component.Runtime == "guo-razor" &&
            component.RuntimeVersion == CapabilityScriptHost.RuntimeVersion, "Unsupported script runtime/version");
        StorePack.Require(component.Capabilities.All(CapabilityScriptHost.Supported), "Unsupported script capability");
        string source = StorePack.ScriptText(pack.ReadPayload(component.Entry, StorePack.MaxScriptBytes));
        // Compilation prepares actions only. It never ticks this temporary runner.
        var check = new ScriptRunner(new CapabilityScriptHost(_host, component.Capabilities, () => true));
        bool valid = check.Start(source);
        string status = check.Status;
        check.Stop();
        StorePack.Require(valid, status);
        return new ScriptPackReview(closure, packId, componentId, version, source, component.Capabilities);
    }

    private ScriptPackReview Current(ScriptPackReview review)
    {
        var current = Review(review.PackId, review.Version, review.ComponentId);
        StorePack.Require(current.ApprovalIdentity == review.ApprovalIdentity, "Pack or dependencies changed; review again");
        return current;
    }

    public void Approve(ScriptPackReview review)
    {
        var current = Current(review);
        _approvals[current.ApprovalIdentity] = current;
    }
    public bool IsApproved(ScriptPackReview review) => _approvals.ContainsKey(review.ApprovalIdentity);
    public bool IsEnabled(string identity) => _enabled.ContainsKey(identity);
    public bool IsEnabled(ScriptPackReview review) => _enabled.TryGetValue(review.Identity, out var active)
        && active.ApprovalIdentity == review.ApprovalIdentity;

    public void Enable(ScriptPackReview review)
    {
        var current = Current(review);
        StorePack.Require(IsApproved(current), "Explicit approval is required before enabling");
        // All validation happens before replacing an existing activation.
        if (_enabled.TryGetValue(current.Identity, out var previous)) _previous[current.Identity] = previous;
        if (_running?.Identity == current.Identity) Stop("Script version changed");
        _enabled[current.Identity] = current;
    }

    /// <summary>True on a shard whose content descriptor forbids script packs (ADR-0026 section 4).</summary>
    public Func<bool> Forbidden { get; set; }

    public void Run(string identity)
    {
        StorePack.Require(Forbidden?.Invoke() != true, "This shard does not allow script packs.");
        StorePack.Require(_enabled.TryGetValue(identity, out var selected), "Enable this script before running");
        var current = Current(selected);
        StorePack.Require(IsApproved(current), "Script approval is required");
        int lease = _lease + 1;
        // Start validates first, without granting the new runner an execution lease.
        bool preparing = true;
        var candidate = new ScriptRunner(new CapabilityScriptHost(_host, current.Grants,
            () => preparing || _lease == lease && IsApproved(current) && _enabled.ContainsKey(identity) && Forbidden?.Invoke() != true));
        StorePack.Require(candidate.Start(current.Source), candidate.Status);
        Stop("Replaced by another script");
        _lease = lease;
        preparing = false;
        _runner = candidate; _running = current;
    }

    public void Tick(long now)
    {
        if (_disposed) return;
        if (Interlocked.Exchange(ref _dirty, 0) != 0) Reconcile();
        _runner?.Tick(now);
    }
    public void Stop(string reason = "Stopped") { _lease++; _runner?.Stop(reason); _running = null; }

    public void Disable(string identity)
    {
        if (_running?.Identity == identity) Stop("Script disabled");
        _enabled.Remove(identity);
    }

    public void Revoke(ScriptPackReview review)
    {
        _approvals.Remove(review.ApprovalIdentity);
        if (_enabled.TryGetValue(review.Identity, out var active) && active.ApprovalIdentity == review.ApprovalIdentity)
            Disable(review.Identity);
    }

    public void Rollback(string identity)
    {
        StorePack.Require(_previous.TryGetValue(identity, out var previous), "No previous activation");
        Enable(previous); // Reverify bytes and approval; never resumes execution automatically.
    }

    public void Reconcile()
    {
        foreach (var active in _approvals.Values.ToArray())
        {
            try { Current(active); }
            catch (Exception e) when (e is IOException or InvalidDataException or FormatException or ArgumentException)
            { Revoke(active); }
        }
    }

    public void Uninstall(string packId, string version)
    {
        foreach (var active in _approvals.Values.Concat(_enabled.Values).Concat(_previous.Values).ToArray())
            if (active.Content.Packs.TryGetValue(packId, out var dependency) && dependency.Version == version)
            { Revoke(active); _previous.Remove(active.Identity); }
        _store.Uninstall(packId, version);
    }

    public void Clear()
    {
        Stop("Session ended"); _enabled.Clear(); _previous.Clear(); _approvals.Clear();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StoreClient.PackChanged -= OnPackChanged;
        Clear(); _store.Dispose();
    }
}
