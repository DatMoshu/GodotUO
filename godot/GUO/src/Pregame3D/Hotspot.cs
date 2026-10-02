// SPDX-License-Identifier: BSD-2-Clause
// GUO addition, not a port: upstream ClassicUO's login screen is 2D gumps.

using System;
using System.Collections.Generic;
using Godot;

namespace GUO.Pregame3D;

/// <summary>
/// A focusable 3D object of the pregame diorama (docs/ui/pregame_3d.md,
/// "Every interactive object is a hotspot"): lifts and rim-glows gold when
/// focused, presses down and up when activated, and is picked by the pointer
/// with a ray against its bounds. Its visual is any node tree; the PSX
/// materials under it carry the rim.
/// </summary>
internal sealed partial class Hotspot : Node3D, IFocusable
{
    /// <summary>How far a focused hotspot rises, in metres.</summary>
    public float LiftHeight { get; set; } = 0.035f;

    /// <summary>How far a press sinks it.</summary>
    public float PressDepth { get; set; } = 0.03f;

    /// <summary>A UO sound played on a press (Client.Game.Audio), or -1 for none.</summary>
    public int PressSound { get; set; } = -1;

    public string Id { get; set; } = "";

    public bool Enabled { get; set; } = true;

    public bool CanFocus => Enabled && IsVisibleInTree();

    public IFocusable Up { get; set; }
    public IFocusable Down { get; set; }
    public IFocusable Left { get; set; }
    public IFocusable Right { get; set; }
    public Action<int> Cycle { get; set; }

    /// <summary>What a press does (after the animation starts; the login itself does not wait for it).</summary>
    public Action Activated { get; set; }

    public bool Focused { get; private set; }

    private Vector3 _rest;
    private float _lift;
    private float _press;
    private Tween _liftTween;
    private Tween _pressTween;
    private Aabb _bounds;
    private bool _boundsSet;

    /// <summary>
    /// Wraps <paramref name="visual"/>, whose transform is in
    /// <paramref name="parent"/>'s space (a child of it, or not parented yet):
    /// the hotspot sits at the parent's origin and the visual keeps its place.
    /// </summary>
    public static Hotspot Wrap(Node3D visual, string id, Node3D parent)
    {
        var h = new Hotspot { Name = "Hotspot_" + id, Id = id };
        parent.AddChild(h);

        visual.GetParent()?.RemoveChild(visual);
        h.AddChild(visual);
        h._rest = h.Position;

        return h;
    }

    /// <summary>Forget the picking bounds (the visual moved within the hotspot): measured again when next needed.</summary>
    public void InvalidateBounds() => _boundsSet = false;

    /// <summary>Picking bounds in this node's space; by default the union of its meshes'.</summary>
    public void SetBounds(Aabb local)
    {
        _bounds = local;
        _boundsSet = true;
    }

    public Aabb Bounds
    {
        get
        {
            if (!_boundsSet)
            {
                _bounds = MeshBounds(this, Transform3D.Identity, true);
                _boundsSet = true;
            }

            return _bounds;
        }
    }

    /// <summary>Distance along the ray to this hotspot's bounds, or null.</summary>
    public float? Pick(Vector3 origin, Vector3 dir)
    {
        if (!CanFocus)
        {
            return null;
        }

        Aabb b = Bounds;

        if (b.Size == Vector3.Zero)
        {
            return null;
        }

        // The ray in the hotspot's own space, against its box (slab test).
        Transform3D inv = GlobalTransform.AffineInverse();
        Vector3 o = inv * origin;
        Vector3 d = inv.Basis * dir;
        float tmin = 0f, tmax = float.MaxValue;

        for (int i = 0; i < 3; i++)
        {
            float lo = b.Position[i], hi = b.End[i];

            if (Mathf.Abs(d[i]) < 1e-6f)
            {
                if (o[i] < lo || o[i] > hi)
                {
                    return null;
                }

                continue;
            }

            float t1 = (lo - o[i]) / d[i], t2 = (hi - o[i]) / d[i];

            if (t1 > t2)
            {
                (t1, t2) = (t2, t1);
            }

            tmin = Mathf.Max(tmin, t1);
            tmax = Mathf.Min(tmax, t2);

            if (tmin > tmax)
            {
                return null;
            }
        }

        return tmin;
    }

    public void SetFocused(bool focused)
    {
        if (Focused == focused)
        {
            return;
        }

        Focused = focused;
        _liftTween?.Kill();
        _liftTween = CreateTween();
        _liftTween.TweenMethod(Callable.From<float>(SetLift), _lift, focused ? LiftHeight : 0f, 0.12);
        SetRim(this, focused ? 0.8f : 0f);
    }

    public void Press()
    {
        if (!Enabled)
        {
            return;
        }

        _pressTween?.Kill();
        _pressTween = CreateTween();
        _pressTween.TweenMethod(Callable.From<float>(SetPress), 0f, PressDepth, 0.06);
        _pressTween.TweenMethod(Callable.From<float>(SetPress), PressDepth, 0f, 0.1);

        if (PressSound >= 0)
        {
            Client.Game?.Audio?.PlaySound(PressSound);
        }

        Activated?.Invoke();
    }

    /// <summary>Moves the resting place (for a hotspot laid out after it was wrapped).</summary>
    public void SetRest(Vector3 position)
    {
        _rest = position;
        Apply();
    }

    private void SetLift(float v)
    {
        _lift = v;
        Apply();
    }

    private void SetPress(float v)
    {
        _press = v;
        Apply();
    }

    private void Apply() => Position = _rest + Vector3.Up * (_lift - _press);

    /// <summary>The rim glow of every PSX material under <paramref name="root"/>.</summary>
    public static void SetRim(Node root, float strength)
    {
        foreach (MeshInstance3D mi in Meshes(root))
        {
            for (int s = 0; s < mi.GetSurfaceOverrideMaterialCount(); s++)
            {
                if (mi.GetSurfaceOverrideMaterial(s) is ShaderMaterial m)
                {
                    m.SetShaderParameter("rim_strength", strength);
                }
            }
        }
    }

    public static IEnumerable<MeshInstance3D> Meshes(Node root)
    {
        if (root is MeshInstance3D mi)
        {
            yield return mi;
        }

        foreach (Node c in root.GetChildren())
        {
            foreach (MeshInstance3D m in Meshes(c))
            {
                yield return m;
            }
        }
    }

    /// <summary>The union of the meshes' boxes under <paramref name="node"/>, in the space <paramref name="toSpace"/> maps from it.</summary>
    public static Aabb MeshBounds(Node node, Transform3D toSpace, bool isRoot = false)
    {
        Aabb result = default;
        bool any = false;

        void Visit(Node n, Transform3D t)
        {
            if (n is MeshInstance3D mi && mi.Mesh != null)
            {
                Aabb box = t * mi.Mesh.GetAabb();
                result = any ? result.Merge(box) : box;
                any = true;
            }

            foreach (Node c in n.GetChildren())
            {
                Transform3D ct = c is Node3D c3 ? t * c3.Transform : t;
                Visit(c, ct);
            }
        }

        Visit(node, toSpace);

        return result;
    }
}
