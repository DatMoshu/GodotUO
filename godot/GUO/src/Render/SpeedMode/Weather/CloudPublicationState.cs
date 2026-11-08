// SPDX-License-Identifier: BSD-2-Clause
namespace GUO.Renderer.SpeedMode.Weather;

// Main-thread only. No engine objects, disposal, callbacks or live scene reads.
internal sealed class CloudPublicationState
{
    internal readonly record struct Ticket(object Owner, long Frame, object World,
        int Width, int Height, object Overlay, long Generation);
    private object _owner, _world;
    private int _width, _height;
    private bool _attempted, _valid, _sampled;
    private Ticket _ticket;
    internal long Frame { get; private set; }

    internal void Begin(object owner, object world, int width, int height)
    {
        Clear();
        Frame++;
        _owner = owner; _world = world; _width = width; _height = height;
    }

    internal bool Publish(Ticket candidate)
    {
        // A departed caller cannot revoke the current scene's accepted output.
        if (!Matches(candidate)) return false;
        if (_attempted) { _valid = false; _ticket = default; return false; }
        _attempted = true;
        if (candidate.Overlay == null || candidate.Generation <= 0 ||
            ReferenceEquals(candidate.Overlay, candidate.World)) return false;
        _ticket = candidate; _valid = true;
        return true;
    }

    internal bool Take(object owner, object world, int width, int height, out Ticket ticket)
    {
        ticket = default;
        if (!_valid || _sampled || !ReferenceEquals(owner, _owner) ||
            !ReferenceEquals(world, _world) || width != _width || height != _height) return false;
        _sampled = true; ticket = _ticket; return true;
    }

    internal bool Revoke(object owner, object overlay, long generation)
    {
        if (!_valid || !ReferenceEquals(owner, _ticket.Owner) ||
            !ReferenceEquals(overlay, _ticket.Overlay) || generation != _ticket.Generation) return false;
        _valid = false; _ticket = default; return true;
    }

    internal void Reject(object owner, long frame)
    {
        if (!ReferenceEquals(owner, _owner) || frame != Frame) return;
        _valid = false; _attempted = true; _ticket = default;
    }

    internal void Clear()
    {
        _owner = _world = null; _ticket = default;
        _width = _height = 0; _valid = _attempted = _sampled = false;
    }

    private bool Matches(Ticket t) => _owner != null && _world != null &&
        ReferenceEquals(t.Owner, _owner) && t.Frame == Frame && ReferenceEquals(t.World, _world) &&
        _width > 0 && _height > 0 && t.Width == _width && t.Height == _height;
}
