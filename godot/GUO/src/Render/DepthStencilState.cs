// SPDX-License-Identifier: BSD-2-Clause

namespace GUO.Renderer
{
    /// <summary>
    /// Upstream's depth-buffer configuration for the world pass.
    /// </summary>
    /// <remarks>
    /// <para>
    /// PORT DEVIATION (GUO): this carries no settings, because nothing can act
    /// on them. ClassicUO sorts alpha-blended world objects with a real depth
    /// buffer, following Shawn Hargreaves' depth-sorting note, which is why
    /// <c>DrawWorld</c> sets <c>DepthStencilState.Default</c> around the render
    /// lists. Godot's 2D canvas has no depth buffer at all: siblings paint in
    /// submission order, full stop.
    /// </para>
    /// <para>
    /// ADR-0001 is what makes that survivable — <c>RenderLists</c> is kept, and
    /// it has already sorted everything by <c>CalculateDepthZ()</c> before the
    /// batcher sees it, so submission order *is* the depth order. The type
    /// exists so the two upstream call sites port unedited; the values name
    /// which upstream state was asked for, and nothing reads them.
    /// </para>
    /// </remarks>
    public sealed class DepthStencilState
    {
        private DepthStencilState(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public static readonly DepthStencilState Default = new DepthStencilState("Default");

        public static readonly DepthStencilState DepthRead = new DepthStencilState("DepthRead");
    }
}
