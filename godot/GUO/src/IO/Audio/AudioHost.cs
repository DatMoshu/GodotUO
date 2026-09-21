using Godot;

namespace GUO.IO.Audio
{
    /// <summary>
    ///     Owns the one scene-tree node every <see cref="Sound" />'s player hangs
    ///     from.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         PORT ADDITION — see ADR-0005. Upstream's <c>SoundInstance</c> is an
    ///         FNA <c>DynamicSoundEffectInstance</c>, which is an ordinary object
    ///         and plays wherever it is made. Godot's equivalent is an
    ///         <see cref="AudioStreamPlayer" />, which is a <see cref="Node" /> and
    ///         plays only from inside the tree — <c>launchers\dev\audio_probe.bat</c>
    ///         measured the refusal: <i>"Playback can only happen when a node is
    ///         inside the scene tree"</i>. <see cref="Sound" /> is not a node, so
    ///         something has to hold the players.
    ///     </para>
    ///     <para>
    ///         The parenting is immediate rather than deferred, so a caller can
    ///         create a player and play it in the same frame, which is what
    ///         <c>Sound.Play</c> does.
    ///     </para>
    /// </remarks>
    internal static class AudioHost
    {
        private static Node _root;

        /// <summary>
        ///     A player already inside the tree, or null if there is no tree to put
        ///     it in — which is every context that is not the running client.
        /// </summary>
        public static AudioStreamPlayer CreatePlayer()
        {
            Node root = Root();

            if (root == null)
            {
                return null;
            }

            var player = new AudioStreamPlayer();
            root.AddChild(player);

            return player;
        }

        private static Node Root()
        {
            if (GodotObject.IsInstanceValid(_root))
            {
                return _root;
            }

            if (Engine.GetMainLoop() is not SceneTree tree || tree.Root == null)
            {
                return null;
            }

            _root = new Node { Name = "Audio" };
            tree.Root.AddChild(_root);

            return _root;
        }
    }
}
