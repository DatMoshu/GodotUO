// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Runtime.CompilerServices;
using Godot;
using GUO.Compat;

namespace GUO.Renderer
{
    /// <summary>
    /// The world viewport's camera: zoom, peek, and the two coordinate
    /// conversions the game asks for constantly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// PORT DEVIATION (GUO): upstream stores the view as an XNA
    /// <c>Matrix</c> -- sixteen floats of which it uses six. Godot's
    /// <see cref="Transform2D"/> is exactly those six, is what the engine
    /// consumes, and inverts without a determinant check, so it replaces the
    /// matrix here. Nothing else changes: every member the ported game code
    /// touches keeps its name, its signature and its behaviour.
    /// </para>
    /// <para>
    /// Measured before changing it: of Camera's 116 references in the ported
    /// tree, none reach <c>ViewTransformMatrix</c> or <c>GetViewport()</c>.
    /// Those two were XNA-facing only, which is why they can become Godot
    /// types without a single call site moving.
    /// </para>
    /// </remarks>
    public class Camera
    {
        private const float MAX_PEEK_DISTANCE = 250f;
        private const float MIN_PEEK_SPEED = 0.01f;
        private const float PEEK_TIME_FACTOR = 5;

        private Transform2D _transform = Transform2D.Identity;
        private Transform2D _inverseTransform = Transform2D.Identity;
        private bool _updateMatrixes = true;
        private float _lerpZoom;
        private float _zoom;
        private Vector2 _lerpOffset;


        public Camera(float minZoomValue = 1f, float maxZoomValue = 1f, float zoomStep = 0.1f)
        {
            ZoomMin = minZoomValue;
            ZoomMax = maxZoomValue;
            ZoomStep = zoomStep;
            Zoom = _lerpZoom = 1f;
        }


        public Rectangle Bounds;

        /// <summary>
        /// World-to-screen transform for the viewport. Replaces upstream's
        /// <c>ViewTransformMatrix</c>; see the type remarks.
        /// </summary>
        public Transform2D ViewTransform
        {
            get
            {
                UpdateMatrices();

                return _transform;
            }
        }

        public float ZoomStep { get; private set; }
        public float ZoomMin { get; private set; }
        public float ZoomMax { get; private set; }
        public float Zoom
        {
            get => _zoom;
            set
            {
                // XNA's MathHelper.Clamp, written out: it clamps to the upper
                // bound first, so when max < min the minimum wins. Math.Clamp
                // throws in that case instead, and Godot's Mathf.Clamp lets
                // the maximum win. Neither is what upstream does.
                float clamped = value > ZoomMax ? ZoomMax : value;
                _zoom = clamped < ZoomMin ? ZoomMin : clamped;
                _updateMatrixes = true;
            }
        }



        public void ZoomIn() => Zoom -= ZoomStep;

        public void ZoomOut() => Zoom += ZoomStep;

        /// <summary>
        /// PORT DEVIATION (GUO): the editor's world view looks at whole
        /// regions, not one screenful, so it widens the range (and the step,
        /// or reaching far out takes a hundred wheel clicks). The game client
        /// keeps the constructor values.
        /// </summary>
        public void SetZoomRange(float min, float max, float step)
        {
            ZoomMin = min;
            ZoomMax = max;
            ZoomStep = step;
            Zoom = _zoom;
        }

        /// <summary>
        /// The camera's rectangle in window space. Replaces upstream's
        /// <c>GetViewport()</c>, which returned an XNA <c>Viewport</c>; the
        /// name also changes because <c>Godot.Viewport</c> is a Node and the
        /// collision would be a trap.
        /// </summary>
        public Rect2I GetViewportRect() => new Rect2I(Bounds.X, Bounds.Y, Bounds.Width, Bounds.Height);

        public Vector2 Offset => _lerpOffset;

        public bool PeekingToMouse;

        public bool PeekBackwards;

        private float _timeDelta = 0;
        private Point _mousePos;

        public void Update(bool force, float timeDelta, Point mousePos)
        {
            if (force)
            {
                _updateMatrixes = true;
            }

            _timeDelta = timeDelta;
            _mousePos = mousePos;

            UpdateMatrices();
        }

        public Point ScreenToWorld(Point point, bool withOffset = false)
        {
            UpdateMatrices();

            int offsetX = 0;
            int offsetY = 0;
            if (withOffset)
            {
                offsetX = -Bounds.X;
                offsetY = -Bounds.Y;
            }

            Transform(ref point, ref _inverseTransform, out point, offsetX, offsetY);

            return point;
        }

        /// <summary>
        ///     Returns screen coordinates from world coordinates.
        ///     There are two variants for screen coordinates:
        ///     1. Relative to the game window (withOffset = true)
        ///     2. Relative to the camera viewport (withOffset = false)
        ///     Because of the fact that the camera viewport content now
        ///     fully scales with the zoom level, everything that
        ///     is not supposed to zoom (UI, etc.) is drawn
        ///     in the UI render target now.
        ///     Only those aspects need to adjust for case 1 above.
        /// </summary>
        public Point WorldToScreen(Point point, bool withOffset = false)
        {
            UpdateMatrices();

            int offsetX = 0;
            int offsetY = 0;
            if (withOffset)
            {
                offsetX = Bounds.X;
                offsetY = Bounds.Y;
            }

            Transform(ref point, ref _transform, out point, offsetX, offsetY);

            return point;
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Transform(ref Point position, ref Transform2D matrix, out Point result, int offsetX, int offsetY)
        {
            // Transform2D holds exactly the six components upstream reads off
            // the 4x4: X = (M11, M12), Y = (M21, M22), Origin = (M41, M42).
            float x = position.X * matrix.X.X + position.Y * matrix.Y.X + matrix.Origin.X + offsetX;
            float y = position.X * matrix.X.Y + position.Y * matrix.Y.Y + matrix.Origin.Y + offsetY;
            result.X = (int) x;
            result.Y = (int) y;
        }

        public Point MouseToWorldPosition()
        {
            Point mouse = _mousePos;

            mouse.X -= Bounds.X;
            mouse.Y -= Bounds.Y;

            return ScreenToWorld(mouse);
        }

        private void UpdateMatrices()
        {
            if (!_updateMatrixes)
            {
                return;
            }

            var origin = new Vector2(Bounds.Width * 0.5f, Bounds.Height * 0.5f);

            CalculateLerpZoom();

            // Peek reads Zoom and writes _lerpOffset, and upstream runs it
            // between the scale multiply and the final translate. Keep that
            // order: the offset it produces is what the translate consumes.
            CalculatePeek(origin);

            // Upstream composes, in XNA's row-vector order:
            //     translate(-origin) * scale(z) * translate(origin - offset)
            // which is an axis-aligned scale plus a constant translation, so
            // the six live components write down directly. Deriving the
            // closed form rather than chaining Godot's column-vector
            // multiplies also removes the step most likely to be silently
            // reversed.
            //
            // The translation is spelled `-(o * z) + o - offset` and NOT the
            // tidier `o * (1 - z) - offset`: those are equal in real
            // arithmetic but not in floats, and the first is the order the
            // matrix chain actually evaluates. Written the tidy way, 30 of
            // 12,600 checked points landed one pixel out after the (int)
            // truncation. Written this way, every one of them is identical.
            float z = _lerpZoom;

            _transform = new Transform2D(
                new Vector2(z, 0f),
                new Vector2(0f, z),
                new Vector2(
                    -(origin.X * z) + origin.X - _lerpOffset.X,
                    -(origin.Y * z) + origin.Y - _lerpOffset.Y
                )
            );

            _inverseTransform = _transform.AffineInverse();

            _updateMatrixes = false;
        }

        private void CalculateLerpZoom()
        {
            float zoom = 1f / Zoom;

            _lerpZoom = zoom;
        }

        private void CalculatePeek(Vector2 origin)
        {
            Vector2 target_offset = new Vector2();

            if (PeekingToMouse)
            {
                Vector2 target = new Vector2(_mousePos.X - Bounds.X, _mousePos.Y - Bounds.Y);

                if (PeekBackwards)
                {
                    target.X = 2 * origin.X - target.X;
                    target.Y = 2 * origin.Y - target.Y;
                }

                target_offset = target - origin;
                float length = target_offset.Length();

                if (length > 0)
                {
                    float length_factor = Math.Min(length / (Bounds.Height >> 1), 1f);
                    target_offset = target_offset.Normalized() * Utility.Easings.OutQuad(length_factor) * MAX_PEEK_DISTANCE / Zoom;
                }
            }

            float dist = target_offset.DistanceTo(_lerpOffset);

            if (dist > 1f)
            {
                float time = Math.Max(Utility.Easings.OutQuart(dist / MAX_PEEK_DISTANCE) * _timeDelta * PEEK_TIME_FACTOR, MIN_PEEK_SPEED);
                _lerpOffset = _lerpOffset.Lerp(target_offset, time);
            }
            else
            {
                _lerpOffset = target_offset;
            }
        }
    }
}
