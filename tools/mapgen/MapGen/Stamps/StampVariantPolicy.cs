using CentrED.MapGen.Passes.Stamp;

namespace CentrED.MapGen.Stamps;

/// <summary>
/// Which stamps may be rotated or mirrored. UO art is drawn for one orientation: a
/// rotated road keeps its east-west edge tiles, a mirrored house has its walls on the
/// wrong side, a rotated shoreline puts the surf on land. Only layouts of
/// orientation-free objects (trees, bushes, rocks) survive a variant, and even then
/// only their positions move — each tree cluster keeps its trunk-to-canopy offsets and
/// land is never painted from a rotated stamp.
/// </summary>
public static class StampVariantPolicy
{
    /// <summary>Kinds whose layout may be rotated/mirrored. Everything else is placed as authored.</summary>
    public static readonly IReadOnlySet<string> OrientationFreeKinds =
        new HashSet<string>(StringComparer.Ordinal) { "forest" };

    public static bool AllowsVariants(string kind) => OrientationFreeKinds.Contains(kind);

    /// <summary>Returns <paramref name="requested"/> when the stamp allows variants, otherwise Original.</summary>
    public static StampVariant Resolve(LoadedStamp stamp, StampVariant requested) =>
        stamp.AllowsVariants ? requested : StampVariant.Original;

    /// <summary>Picks a random variant when allowed (consumes one RNG draw only when allowed).</summary>
    public static StampVariant Pick(LoadedStamp stamp, Random rng, bool useVariants) =>
        useVariants && stamp.AllowsVariants ? (StampVariant)rng.Next(8) : StampVariant.Original;
}
