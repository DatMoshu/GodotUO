// SPDX-License-Identifier: BSD-2-Clause
using System;

namespace GUO.Renderer.SpeedMode.Weather;

// Pure source regression: no Godot, filesystem, profile or network dependencies.
internal static class CloudPublicationStateRegression
{
    internal static void Run()
    {
        var state = new CloudPublicationState();
        object owner = new(), world = new(), overlay = new(), nextOwner = new(), nextOverlay = new();
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException("cloud publication: " + name); }
        CloudPublicationState.Ticket Current() => new(owner, state.Frame, world, 1280, 720, overlay, 1);
        bool Take() => state.Take(owner, world, 1280, 720, out _);

        state.Begin(owner, world, 1280, 720);
        Check(!Take(), "no publication / early return");
        var first = Current();
        Check(state.Publish(first) && Take(), "valid matching frame");
        Check(!Take(), "single sample");
        state.Begin(owner, world, 1280, 720);
        Check(!state.Publish(first) && !Take(), "stale frame");
        Check(!state.Publish(Current() with { Owner = nextOwner }) && !Take(), "owner mismatch");
        Check(!state.Publish(Current() with { World = new object() }) && !Take(), "target replacement");
        Check(!state.Publish(Current() with { Width = 960 }) && !Take(), "size mismatch");
        Check(state.Publish(Current()), "invalid foreign tickets do not consume current slot");
        Check(!state.Publish(Current()) && !Take(), "duplicate publication revokes ambiguous output");
        state.Begin(owner, world, 1280, 720);
        Check(!state.Publish(Current() with { Generation = 0 }) && !Take(), "invalid generation");
        state.Begin(owner, world, 1280, 720);
        Check(state.Publish(Current()), "published before disable");
        state.Clear();
        Check(!Take() && !state.Publish(Current()), "disabled / queued unload cannot republish");
        state.Begin(nextOwner, world, 1280, 720);
        var next = new CloudPublicationState.Ticket(nextOwner, state.Frame, world, 1280, 720, nextOverlay, 2);
        Check(state.Publish(next), "new scene reuse");
        Check(!state.Revoke(owner, overlay, 1), "old release cannot clear new scene");
        Check(!state.Publish(first), "old queued publication rejected after scene reuse");
        Check(state.Take(nextOwner, world, 1280, 720, out var taken) && taken == next, "new publication retained");
        Check(state.Revoke(nextOwner, nextOverlay, 2), "current owner release after sample");
        state.Begin(owner, world, 1280, 720);
        Check(state.Publish(Current() with { Generation = 3 }), "same owner new generation");
        Check(!state.Revoke(owner, overlay, 1) && Take(), "old generation release cannot clear current");
        state.Begin(owner, world, 1280, 720);
        Check(state.Publish(Current()), "published before failed target validation");
        state.Reject(nextOwner, state.Frame);
        Check(Take(), "foreign rejection cannot clear current scene");
        state.Reject(owner, state.Frame);
        Check(!Take(), "current target failure revokes sampled publication");
        state.Begin(owner, world, 1280, 720);
        Check(!state.Publish(Current() with { Overlay = world }) && !Take(), "world cannot be cloud foreground");
        state.Begin(owner, world, 960, 640);
        Check(!state.Publish(Current()) && !Take(), "resize rejects former dimensions");
    }
}
