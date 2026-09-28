// GUO-owned (ADR-0023): the "-postfx" client command.

using System;
using System.Linq;
using GUO.Game;
using GUO.Game.Managers;

namespace GUO.Renderer.PostFx
{
    /// <summary>
    /// Typed in the chat as a client command (the "-" prefix, as ClassicUO's own
    /// commands): <c>-postfx list</c>, <c>-postfx load NAME</c>, <c>-postfx off</c>,
    /// <c>-postfx save NAME</c>, <c>-postfx split 0.5</c>, <c>-postfx menu</c>, <c>-postfx cost</c>.
    /// </summary>
    internal static class PostFxCommand
    {
        public static void Run(string[] args)
        {
            string verb = args.Length > 1 ? args[1].ToLowerInvariant() : "menu";
            string rest = args.Length > 2 ? string.Join(' ', args.Skip(2)) : "";
            PostFxStack stack = PostFxStack.Instance;

            switch (verb)
            {
                case "list":
                    Say("presets: " + string.Join(", ", PostFxLibrary.Presets().Select(p => p.Name)));
                    Say("shaders: " + string.Join(", ", PostFxLibrary.ShaderNames()));
                    break;
                case "load":
                    PostFxPreset p = PostFxLibrary.Find(rest);
                    if (p == null)
                    {
                        Say($"no preset \"{rest}\" (-postfx list)");
                        break;
                    }

                    stack.Use(p);
                    Say($"look: {p.Name}");
                    break;
                case "off":
                case "classic":
                    stack.Use(PostFxPreset.Classic());
                    Say("look: Classic");
                    break;
                case "save":
                    if (string.IsNullOrWhiteSpace(rest))
                    {
                        Say("-postfx save NAME");
                        break;
                    }

                    PostFxPreset copy = stack.Preset.Clone();
                    copy.Name = rest;
                    Say($"saved {PostFxLibrary.Save(copy)}");
                    stack.Use(copy);
                    break;
                case "split":
                    stack.Split = float.TryParse(rest, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float s)
                        ? Math.Clamp(s, 0f, 1f)
                        : stack.Split > 0 ? 0f : 0.5f;
                    stack.Rebuild();
                    Say($"A/B split: {(stack.Split > 0 ? $"Classic left of {stack.Split:0.00}" : "off")}");
                    break;
                case "cost":
                    Say($"post-processing GPU time last frame: {stack.LastGpuMs:0.000} ms ({stack.Preset.Name})");
                    break;
                default:
                    PostFxMenu.Toggle();
                    break;
            }
        }

        private static void Say(string text)
        {
            GameActions.Print(GUO.Client.Game?.UO?.World, "[postfx] " + text);
        }
    }
}
