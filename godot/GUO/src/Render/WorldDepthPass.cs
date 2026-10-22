// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using Godot;

namespace GUO.Renderer
{
    /// <summary>
    /// PERF-GPU step 1 (ADR-0007, amendment 2026-10-19): the world pass drawn
    /// into a 3D <see cref="SubViewport"/>, where the GPU orders quads by a
    /// depth key instead of the CPU sort ordering canvas commands.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every quad is one instance of a <see cref="MultiMesh"/>, one MultiMesh
    /// per texture (atlas page). The vertex shader places it in the render
    /// target's pixels through the same <see cref="Transform2D"/> the canvas
    /// world pass puts on its canvas item, and writes the depth key as clip z.
    /// Within one page the instances keep their submission order, so equal
    /// keys resolve as the sort's insertion order does, provided the depth
    /// test passes on equal (the depth probe checks which way it goes).
    /// </para>
    /// <para>
    /// Step 1 holds only what the depth probe needs; nothing in the client
    /// draws through it yet, and <c>--gpu-world</c> does not exist until step 2.
    /// </para>
    /// </remarks>
    public sealed class WorldDepthPass : IDisposable
    {
        private const string SHADER_PATH = "res://src/Render/shaders/world_depth.gdshader";

        // 12 floats of transform, 4 of colour, 4 of custom data.
        private const int Stride = 20;

        private static Shader _shader;

        private readonly SubViewport _viewport;
        private readonly Node3D _root;
        private readonly ArrayMesh _quad;
        private readonly Dictionary<Texture2D, Page> _pages = new();
        private readonly List<Page> _order = new();

        private Transform2D _view = Transform2D.Identity;

        public WorldDepthPass(Node parent, Vector2I size)
        {
            _shader ??= GD.Load<Shader>(SHADER_PATH);

            _viewport = new SubViewport
            {
                Size = size,
                OwnWorld3D = true,
                TransparentBg = true,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
                Msaa3D = Godot.Viewport.Msaa.Disabled,
                ScreenSpaceAA = Godot.Viewport.ScreenSpaceAAEnum.Disabled,
                UseTaa = false,
                Scaling3DScale = 1f,
            };

            // The vertex shader writes clip space itself; the camera only has
            // to exist for the viewport to draw its 3D world at all.
            var camera = new Camera3D
            {
                Projection = Camera3D.ProjectionType.Orthogonal,
                Size = 2f,
                Near = 0.05f,
                Far = 10f,
                Current = true,
                Environment = new Godot.Environment
                {
                    BackgroundMode = Godot.Environment.BGMode.ClearColor,
                    TonemapMode = Godot.Environment.ToneMapper.Linear,
                },
            };

            _root = new Node3D();
            _viewport.AddChild(camera);
            _viewport.AddChild(_root);
            parent.AddChild(_viewport);

            using var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            // The canvas renderer's rect: corners top-left, bottom-left,
            // bottom-right, top-right; triangles 0 1 2 and 0 2 3.
            arrays[(int)Mesh.ArrayType.Vertex] = new[]
            {
                new Vector3(0, 0, 0), new Vector3(0, 1, 0), new Vector3(1, 1, 0), new Vector3(1, 0, 0),
            };
            arrays[(int)Mesh.ArrayType.Index] = new[] { 0, 1, 2, 0, 2, 3 };

            _quad = new ArrayMesh();
            _quad.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        }

        public SubViewport Viewport => _viewport;

        /// <summary>
        /// Clip y sign; see world_depth.gdshader. Forward+ and Compatibility
        /// both measured -1 (docs/perf/2026-10-19_depth_probe.md); the probe tries both.
        /// </summary>
        public float FlipY { get; set; } = -1f;

        /// <summary>True when the renderer's depth is reverse Z (larger nearer): both renderers measured it; the probe tries both.</summary>
        public bool ReverseZ { get; set; } = true;

        public void Begin(Transform2D view)
        {
            _view = view;

            foreach (Page page in _order)
            {
                page.Count = 0;
            }
        }

        /// <summary>
        /// One quad: <paramref name="dest"/> in world pixels, <paramref name="source"/>
        /// in texture pixels (as CanvasItemAddTextureRectRegion takes them),
        /// <paramref name="key"/> in 0..1, larger nearer.
        /// </summary>
        public void AddQuad(Texture2D texture, Rect2 dest, Rect2 source, float key)
        {
            // texpixel_size and the region product exactly as the canvas
            // renderer computes them (renderer_canvas_render_rd.cpp).
            float tx = (float)(1.0 / (float)texture.GetWidth());
            float ty = (float)(1.0 / (float)texture.GetHeight());
            var uv = new Vector4(source.Position.X * tx, source.Position.Y * ty, source.Size.X * tx, source.Size.Y * ty);

            if (!_pages.TryGetValue(texture, out Page page))
            {
                page = new Page(texture, _quad, _root);
                _pages.Add(texture, page);
                _order.Add(page);
            }

            page.Append(dest, uv, key);
        }

        public void End()
        {
            Vector2I size = _viewport.Size;

            foreach (Page page in _order)
            {
                page.Upload(_view, size, FlipY, ReverseZ);
            }
        }

        public void Dispose()
        {
            foreach (Page page in _order)
            {
                page.Dispose();
            }

            _pages.Clear();
            _order.Clear();
            _viewport.QueueFree();
        }

        private sealed class Page : IDisposable
        {
            private readonly MultiMesh _multimesh;
            private readonly MultiMeshInstance3D _instance;
            private readonly ShaderMaterial _material;
            private float[] _buffer = Array.Empty<float>();

            public int Count;

            public Page(Texture2D texture, Mesh quad, Node3D root)
            {
                _multimesh = new MultiMesh
                {
                    TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                    UseColors = true,
                    UseCustomData = true,
                    Mesh = quad,
                    // Positions are not in world space at all; never cull.
                    CustomAabb = new Aabb(new Vector3(-1e7f, -1e7f, -1e7f), new Vector3(2e7f, 2e7f, 2e7f)),
                };

                _material = new ShaderMaterial { Shader = _shader };
                _material.SetShaderParameter("page", texture);

                _instance = new MultiMeshInstance3D
                {
                    Multimesh = _multimesh,
                    MaterialOverride = _material,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                };

                root.AddChild(_instance);
            }

            public void Append(Rect2 dest, Vector4 uv, float key)
            {
                int at = Count * Stride;

                if (at + Stride > _buffer.Length)
                {
                    Array.Resize(ref _buffer, Math.Max(Stride * 64, _buffer.Length * 2));
                }

                // The Transform3D rows as RenderingServer lays them out:
                // basis row 0 + origin.x, row 1 + origin.y, row 2 + origin.z.
                Span<float> f = _buffer.AsSpan(at, Stride);
                f.Clear();
                f[0] = dest.Size.X;
                f[3] = dest.Position.X;
                f[5] = dest.Size.Y;
                f[7] = dest.Position.Y;
                f[11] = key;
                f[12] = 1f;
                f[13] = 1f;
                f[14] = 1f;
                f[15] = 1f;
                f[16] = uv.X;
                f[17] = uv.Y;
                f[18] = uv.Z;
                f[19] = uv.W;
                Count++;
            }

            public void Upload(Transform2D view, Vector2I size, float flipY, bool reverseZ)
            {
                _material.SetShaderParameter("view_x", view.X);
                _material.SetShaderParameter("view_y", view.Y);
                _material.SetShaderParameter("view_origin", view.Origin);
                // Transform3D.translate_local(-(w / 2), -(h / 2), 0) then scale(2 / w, 2 / h),
                // in float, as the canvas renderer builds its screen transform.
                float sx = 2.0f / size.X;
                float sy = 2.0f / size.Y;
                _material.SetShaderParameter("screen", new Vector4(sx, sy, -(size.X / 2.0f) * sx, -(size.Y / 2.0f) * sy));
                _material.SetShaderParameter("flip_y", flipY);
                _material.SetShaderParameter("key_reversed", reverseZ ? 1f : 0f);

                int capacity = _buffer.Length / Stride;

                if (_multimesh.InstanceCount != capacity)
                {
                    _multimesh.InstanceCount = capacity;
                }

                _multimesh.VisibleInstanceCount = Count;

                if (capacity > 0)
                {
                    RenderingServer.MultimeshSetBuffer(_multimesh.GetRid(), _buffer);
                }
            }

            public void Dispose()
            {
                _instance.QueueFree();
            }
        }
    }
}
