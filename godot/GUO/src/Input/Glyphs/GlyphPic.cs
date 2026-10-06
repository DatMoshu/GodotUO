// GUO addition, not a port: upstream ClassicUO has no gamepad, so no gump of
// its own ever draws a button glyph.

using GUO.Compat;
using GUO.Game.Scenes;
using GUO.Game.UI.Controls;
using GUO.Renderer;

namespace GUO.Input.Glyphs
{
    /// <summary>
    /// A pad button's glyph inside a classic gump (the Options' "Controller
    /// buttons" legend), drawn by the gump batcher like any gump art. It asks
    /// for its texture on every draw, so it follows the pad last used, its
    /// family and its layout without being rebuilt.
    /// </summary>
    internal sealed class GlyphPic : Control
    {
        /// <summary>The legend: each job a pad does, in the client's words.</summary>
        public static readonly (PadAction action, string words)[] Legend =
        {
            (PadAction.Confirm, "Click at the pointer"),
            (PadAction.Cancel, "Cancel (Escape)"),
            (PadAction.WindowMenu, "Attack last (the window menu on touch screens)"),
            (PadAction.Y, "Macro row"),
            (PadAction.Walk, "Walk (or the left stick)"),
            (PadAction.Pointer, "Move the pointer"),
            (PadAction.Back, "Side panel, open or closed (one screen)"),
        };

        private readonly PadAction _action;

        public GlyphPic(PadAction action, int size = InputGlyphs.Size)
        {
            _action = action;
            Width = size;
            Height = size;
            AcceptMouseInput = false;
        }

        /// <summary>For the probe: the glyph drawn last, or null.</summary>
        public Godot.Texture2D Drawn { get; private set; }

        public override bool AddToRenderLists(RenderLists renderLists, int x, int y, ref float layerDepthRef)
        {
            Godot.Texture2D texture = InputGlyphs.Pad(_action);
            Drawn = texture;

            if (texture == null)
            {
                return false;
            }

            float layerDepth = layerDepthRef;
            Vector3 hueVector = ShaderHueTranslator.GetHueVector(0, false, Alpha);

            renderLists.AddGumpNoAtlas(batcher =>
            {
                batcher.Draw(texture, new Rectangle(x, y, Width, Height), hueVector, layerDepth);

                return true;
            });

            return true;
        }
    }
}
