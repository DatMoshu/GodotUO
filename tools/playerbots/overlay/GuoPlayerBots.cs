// GUO integration for UO Offline PlayerBots. BSD-2-Clause.
namespace Server.CustomBots;

public static class GuoPlayerBots
{
    public static void Configure()
    {
        // Configure runs before BotStartupManager.Initialize creates spawners.
        BotPopulation.TargetCount = System.Math.Clamp(
            ServerConfiguration.GetOrUpdateSetting("guo.playerbots.population", 50), 1, 1600);
    }
}

public static class GuoPathFollowerCompatibility
{
    // ModernUO e07416902 removed the run argument: the Running bit now comes
    // from actual step pace. Preserve that behavior while accepting the bot API.
    public static bool Follow(this PathFollower follower, bool run, int range) =>
        follower.Follow(range);
}
