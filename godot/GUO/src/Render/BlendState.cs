// SPDX-License-Identifier: BSD-2-Clause

namespace GUO.Renderer
{
    /// <summary>
    /// One input to the blend equation.
    /// </summary>
    /// <remarks>
    /// PORT DEVIATION (GUO): this is
    /// <c>Microsoft.Xna.Framework.Graphics.Blend</c>, name for name and in
    /// upstream's declaration order, so the ported effect views construct
    /// their blend states unchanged. The order matters: the values are what
    /// <c>uo_hue_blend.gdshader</c> branches on, and the two lists have to
    /// agree.
    /// </remarks>
    public enum Blend
    {
        One,
        Zero,
        SourceColor,
        InverseSourceColor,
        SourceAlpha,
        InverseSourceAlpha,
        DestinationColor,
        InverseDestinationColor,
        DestinationAlpha,
        InverseDestinationAlpha,

        // XNA has these two as well. Nothing in ClassicUO uses either, and a
        // constant blend colour would need plumbing that has no caller, so
        // they are named here and rejected by the batcher rather than
        // silently treated as something else.
        BlendFactor,
        InverseBlendFactor,
        SourceAlphaSaturation,
    }

    /// <summary>How the two weighted terms are combined.</summary>
    public enum BlendFunction
    {
        Add,
        Subtract,
        ReverseSubtract,
        Min,
        Max,
    }

    /// <summary>
    /// How a sprite is combined with what is already on screen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// PORT DEVIATION (GUO): upstream uses XNA's <c>BlendState</c>, which is a
    /// handle the graphics device consumes. This is the same six properties
    /// with the same names and the same defaults, but it is plain data: the
    /// batcher reads it and pushes the six values into
    /// <c>uo_hue_blend.gdshader</c>, which evaluates the equation itself.
    /// </para>
    /// <para>
    /// That indirection exists because Godot's canvas has five fixed blend
    /// modes and ClassicUO's effects need equations outside them — see
    /// ADR-0003. Reimplementing the fixed-function unit rather than mapping
    /// each state to its nearest Godot mode means new upstream blend states
    /// keep working without anyone noticing they had to.
    /// </para>
    /// <para>
    /// The defaults are XNA's: <c>new BlendState()</c> is opaque, (One, Zero,
    /// Add). Upstream's effect states set only the colour triple and rely on
    /// that for alpha, so getting the defaults right is not cosmetic.
    /// </para>
    /// </remarks>
    public sealed class BlendState
    {
        public Blend ColorSourceBlend { get; set; } = Blend.One;

        public Blend ColorDestinationBlend { get; set; } = Blend.Zero;

        public BlendFunction ColorBlendFunction { get; set; } = BlendFunction.Add;

        public Blend AlphaSourceBlend { get; set; } = Blend.One;

        public Blend AlphaDestinationBlend { get; set; } = Blend.Zero;

        public BlendFunction AlphaBlendFunction { get; set; } = BlendFunction.Add;

        /// <summary>
        /// Premultiplied alpha blending: (One, InverseSourceAlpha). This is
        /// what upstream draws everything under, and what the ordinary
        /// <c>uo_hue.gdshader</c> gets from <c>blend_premul_alpha</c> without
        /// any of the by-hand machinery.
        /// </summary>
        public static readonly BlendState AlphaBlend = new BlendState
        {
            ColorSourceBlend = Blend.One,
            ColorDestinationBlend = Blend.InverseSourceAlpha,
            AlphaSourceBlend = Blend.One,
            AlphaDestinationBlend = Blend.InverseSourceAlpha,
        };

        public static readonly BlendState Additive = new BlendState
        {
            ColorSourceBlend = Blend.SourceAlpha,
            ColorDestinationBlend = Blend.One,
            AlphaSourceBlend = Blend.SourceAlpha,
            AlphaDestinationBlend = Blend.One,
        };

        public static readonly BlendState Opaque = new BlendState();

        /// <summary>
        /// True when this is XNA's <see cref="Additive"/>, which is Godot's own
        /// blend_add, so the batcher can let the hardware do it.
        /// </summary>
        internal bool IsAdditive =>
            ColorSourceBlend == Blend.SourceAlpha
            && ColorDestinationBlend == Blend.One
            && ColorBlendFunction == BlendFunction.Add
            && AlphaSourceBlend == Blend.SourceAlpha
            && AlphaDestinationBlend == Blend.One
            && AlphaBlendFunction == BlendFunction.Add;

        /// <summary>
        /// True when this is the blend the plain sprite shader already does in
        /// hardware, so the batcher can skip the back-buffer read entirely.
        /// </summary>
        internal bool IsPremultipliedAlpha =>
            ColorSourceBlend == Blend.One
            && ColorDestinationBlend == Blend.InverseSourceAlpha
            && ColorBlendFunction == BlendFunction.Add
            && AlphaSourceBlend == Blend.One
            && AlphaDestinationBlend == Blend.InverseSourceAlpha
            && AlphaBlendFunction == BlendFunction.Add;
    }
}
