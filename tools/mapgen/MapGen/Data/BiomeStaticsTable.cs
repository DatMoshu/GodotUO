using System.Xml.Linq;
using CentrED.MapGen.IR;

namespace CentrED.MapGen.Data;

// F-9: parallel data structure to TileTables.ForestSpecies that stores multi-tile
// "static groups" (e.g. "tree trunk + canopy", "mushroom cluster") with Freq weights.
// Populated by importing uo-landscaper-mod (norad32) <Data/Statics/*.xml> files.
//
// Norad XML format (one file per biome):
//   <RandomStatics Chance="15">           <!-- per-cell placement chance, %  -->
//     <Statics Description="..." Freq="10">     <!-- group, weighted -->
//       <Static TileID="3484" X="0" Y="0" Z="0" Hue="0"/>
//       <Static TileID="3486" X="0" Y="0" Z="0" Hue="0"/>
//     </Statics>
//     ...
//   </RandomStatics>
public sealed class BiomeStaticsTable
{
    public sealed class StaticTile
    {
        public ushort TileID;
        public sbyte Xoff;
        public sbyte Yoff;
        public sbyte Zoff;
        public ushort Hue;
    }

    public sealed class StaticGroup
    {
        public string Description = "";
        public int Freq;
        public List<StaticTile> Tiles = new();
    }

    public sealed class BiomeCatalogue
    {
        public BiomeId Biome;
        public int ChancePercent;            // 0..100, per-cell placement chance
        public List<StaticGroup> Groups = new();
        public int TotalFreq;                // sum of Freq for weighted selection

        public StaticGroup? PickGroup(Random rng)
        {
            if (Groups.Count == 0 || TotalFreq <= 0) return null;
            int roll = rng.Next(TotalFreq);
            int acc = 0;
            foreach (var g in Groups)
            {
                acc += g.Freq;
                if (roll < acc) return g;
            }
            return Groups[^1];
        }
    }

    public Dictionary<BiomeId, BiomeCatalogue> ByBiome { get; } = new();

    public static BiomeStaticsTable Empty { get; } = new();

    // Loads every <biomeName>.xml under the supplied root directory using the default
    // filename → BiomeId mapping. Files whose name doesn't map are skipped (with the
    // unknown-name list returned for diagnostics).
    public static BiomeStaticsTable LoadFromNorad(string staticsRoot, out List<string> unknownNames)
    {
        unknownNames = new List<string>();
        var t = new BiomeStaticsTable();
        if (!Directory.Exists(staticsRoot)) return t;

        foreach (var xmlFile in Directory.EnumerateFiles(staticsRoot, "*.xml"))
        {
            var name = Path.GetFileNameWithoutExtension(xmlFile);
            if (!DefaultNameMap.TryGetValue(name, out var biome))
            {
                unknownNames.Add(name);
                continue;
            }
            var cat = LoadFile(xmlFile, biome);
            if (cat != null) t.ByBiome[biome] = cat;
        }

        // Alias fallback: biomes that don't ship with a dedicated XML reuse a thematic
        // sibling's catalogue so they're never silently empty. Drop these entries as soon
        // as a real {Savanna,Tundra,Wetland}.xml is authored.
        FillAlias(t, BiomeId.Wetland,  BiomeId.Swamp);
        FillAlias(t, BiomeId.Tundra,   BiomeId.Snow);
        FillAlias(t, BiomeId.Savanna,  BiomeId.Grassland);
        FillAlias(t, BiomeId.HighMountain, BiomeId.Mountain);

        return t;
    }

    private static void FillAlias(BiomeStaticsTable t, BiomeId target, BiomeId source)
    {
        if (t.ByBiome.ContainsKey(target)) return;
        if (!t.ByBiome.TryGetValue(source, out var src)) return;
        // Shallow-copy reference is fine: scatter is read-only at runtime.
        t.ByBiome[target] = new BiomeCatalogue
        {
            Biome = target,
            ChancePercent = src.ChancePercent,
            Groups = src.Groups,
            TotalFreq = src.TotalFreq,
        };
    }

    private static readonly Dictionary<string, BiomeId> DefaultNameMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // Beach uses Beach.xml. Sand.xml (cacti/yucca/bones/oasis) is Desert content,
        // NOT Beach — the prior "Sand" → Beach mapping caused Sand.xml (Chance=0) to
        // alphabetically overwrite Beach.xml (Chance=1) at load time, silently
        // disabling all Beach scatter. Now Sand → Desert as intended.
        ["Beach"] = BiomeId.Beach,
        ["Sand"] = BiomeId.Desert,
        ["Cave"] = BiomeId.Cave,
        ["Forest"] = BiomeId.Forest,
        ["Forest Yew"] = BiomeId.DenseForest,
        ["Goblin Forest"] = BiomeId.DenseForest,
        ["Goblin Forest Vyseky"] = BiomeId.DenseForest,
        ["Furrows"] = BiomeId.Grassland,
        ["Grass"] = BiomeId.Grassland,
        ["Savanna"] = BiomeId.Savanna,
        ["Jungle"] = BiomeId.Jungle,
        ["Lava"] = BiomeId.Lava,
        ["Snow"] = BiomeId.Snow,
        ["Tundra"] = BiomeId.Tundra,
        ["Swamp"] = BiomeId.Swamp,
        // Wetland is Swamp's lighter sibling — reuses Swamp.xml (lilypads, reeds,
        // cattails) instead of needing a dedicated catalogue.
        ["Wetland"] = BiomeId.Wetland,
    };

    private static BiomeCatalogue? LoadFile(string xmlPath, BiomeId biome)
    {
        XDocument doc;
        try { doc = XDocument.Load(xmlPath); }
        catch { return null; }
        var root = doc.Root;
        if (root == null) return null;

        var cat = new BiomeCatalogue { Biome = biome };
        if (int.TryParse((string?)root.Attribute("Chance"), out var chance)) cat.ChancePercent = chance;

        foreach (var s in root.Elements("Statics"))
        {
            var group = new StaticGroup
            {
                Description = (string?)s.Attribute("Description") ?? "",
                Freq = int.TryParse((string?)s.Attribute("Freq"), out var f) ? f : 0,
            };
            if (group.Freq <= 0) continue; // Norad uses Freq=0 to mark disabled groups

            foreach (var t in s.Elements("Static"))
            {
                if (!ushort.TryParse((string?)t.Attribute("TileID"), out var tid)) continue;
                var st = new StaticTile { TileID = tid };
                if (int.TryParse((string?)t.Attribute("X"), out var xo)) st.Xoff = (sbyte)Math.Clamp(xo, sbyte.MinValue, sbyte.MaxValue);
                if (int.TryParse((string?)t.Attribute("Y"), out var yo)) st.Yoff = (sbyte)Math.Clamp(yo, sbyte.MinValue, sbyte.MaxValue);
                if (int.TryParse((string?)t.Attribute("Z"), out var zo)) st.Zoff = (sbyte)Math.Clamp(zo, sbyte.MinValue, sbyte.MaxValue);
                if (ushort.TryParse((string?)t.Attribute("Hue"), out var hue)) st.Hue = hue;
                group.Tiles.Add(st);
            }
            if (group.Tiles.Count == 0) continue;
            cat.Groups.Add(group);
            cat.TotalFreq += group.Freq;
        }
        return cat;
    }
}
