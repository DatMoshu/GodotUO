// SPDX-License-Identifier: BSD-2-Clause
using System;
using Godot;
namespace GUO.Renderer.DepthWorld;

// A native object/method signal connection remains identifiable while Godot
// serializes managed delegates for assembly reload. The capture owns its lifetime.
[Tool]
internal partial class OrderedWorldDrawHook : RefCounted
{
    internal Action Draw;
    public void Dispatch() => Draw?.Invoke();
}
