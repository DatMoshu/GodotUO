// GUO-owned (ADR-0023): the game-state values a preset's bindings can read.

using System;
using GUO.Game;

namespace GUO.Renderer.PostFx
{
    /// <summary>
    /// Named values from game state, each a plain float, for preset bindings
    /// ("param = offset + scale * value"). Every value is read fresh each frame
    /// and nothing is stored: a binding never carries state between frames.
    /// </summary>
    internal static class PostFxState
    {
        public static readonly string[] Names =
        {
            "hp_ratio", "hp_missing", "stamina_ratio", "mana_ratio", "light_level", "war_mode", "weather", "time",
        };

        /// <summary>The value, or null when there is no world or no player to read it from.</summary>
        public static float? Get(string name)
        {
            if (name == "time")
            {
                return (float)(GUO.Time.Ticks / 1000.0);
            }

            World world = GUO.Client.Game?.UO?.World;
            var player = world?.Player;

            if (world == null || player == null)
            {
                return null;
            }

            switch (name)
            {
                case "hp_ratio":
                    return Ratio(player.Hits, player.HitsMax);
                case "hp_missing":
                    return 1f - Ratio(player.Hits, player.HitsMax);
                case "stamina_ratio":
                    return Ratio(player.Stamina, player.StaminaMax);
                case "mana_ratio":
                    return Ratio(player.Mana, player.ManaMax);
                case "light_level":
                    // UO light runs 0 (bright) .. 0x1F (dark); the level the world
                    // is actually drawn at is the darker of overall and personal.
                    return Math.Clamp(Math.Min(world.Light.Overall, world.Light.Personal) / 31f, 0f, 1f);
                case "war_mode":
                    return player.InWarMode ? 1f : 0f;
                case "weather":
                    return world.Weather.CurrentWeather switch
                    {
                        WeatherType.WT_RAIN => 1f,
                        WeatherType.WT_STORM_APPROACH or WeatherType.WT_STORM_BREWING => 2f,
                        WeatherType.WT_SNOW => 3f,
                        _ => 0f,
                    };
                default:
                    return null;
            }
        }

        private static float Ratio(int value, int max) => max <= 0 ? 1f : Math.Clamp(value / (float)max, 0f, 1f);
    }
}
