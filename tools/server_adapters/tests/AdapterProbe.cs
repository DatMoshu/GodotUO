// SPDX-License-Identifier: BSD-2-Clause
// Executed against real upstream core and Scripts assemblies, never a mock server API.
using System;
using System.IO;
using System.Linq;
using Server;
using GUO.Content;
public static class AdapterProbe
{
    static void Check(bool condition,string message) { if(!condition) throw new Exception(message); }
    public static int Main()
    {
        try {
            Core.DataDirectories.Add(Environment.GetEnvironmentVariable("UO_CLIENT_DATA"));
            Server.Misc.MapDefinitions.Configure();
#if RUNUO
            ScriptCompiler.Assemblies=new System.Reflection.Assembly[] { typeof(Deployment).Assembly };
#endif
            Deployment.Configure();
            Check(TileData.ItemTable[6001].Height==5, "tile metadata must precede world load");
            World.Load();
            Deployment.Initialize();
            Check(Deployment.Identity==new string('a',64),"deployment identity");
            Item original=Deployment.Items["test:stone"].Create();
            Check(original.ItemID==6001 && original.Name=="Test stone" && original.Weight==1.5 && original.Movable,"item definition");
            var stream=new MemoryStream();var writer=new BinaryFileWriter(stream,true);original.Serialize(writer);writer.Flush();stream.Position=0;
            var restored=new PackItem((Serial)0x40001000);restored.Deserialize(new BinaryFileReader(new BinaryReader(stream)));
            Check(restored.Identity=="test:stone" && restored.ItemID==original.ItemID && restored.Weight==original.Weight,"item persistence");
            restored.Delete();original.Delete();stream.Dispose();
            foreach(double roll in new[] {0.0,0.999999999}) { var items=Deployment.Roll(Deployment.Loot["test:loot"],delegate {return roll;});Check(items.Count==(roll==0?1:2),"loot bounds");foreach(Item i in items)i.Delete(); }
            Check(TileData.ItemTable[6001].Height==5 && TileData.ItemTable[6001].Impassable,"tiledata collision metadata");
            Check(Map.Felucca.Tiles.GetLandTile(80,80).ID==3,"land override");
            Check(Map.Felucca.Tiles.GetStaticBlock(10,10)[1][1].Single().ID==6001,"static override");
            Check(Map.Felucca.Regions.ContainsKey("GUO test region"),"region registration");
            Item unrelated=new Item(1);Deployment.ApplyDecorations("test:decor");
            var owned=World.Items.Values.OfType<PackDecoration>().Single(i=>!i.Deleted);Serial serial=owned.Serial;
            Deployment.ApplyDecorations("test:decor");Check(World.Items.Values.OfType<PackDecoration>().Count(i=>!i.Deleted)==1 && !owned.Deleted && owned.Serial==serial,"decoration idempotence");
            stream=new MemoryStream();writer=new BinaryFileWriter(stream,true);owned.Serialize(writer);writer.Flush();stream.Position=0;
            var decoration=new PackDecoration((Serial)0x40001001);decoration.Deserialize(new BinaryFileReader(new BinaryReader(stream)));
            Check(decoration.Group=="test:decor" && decoration.Key=="one","decoration ownership persistence");decoration.Delete();stream.Dispose();
            Deployment.RemoveDecorations("test:decor");Check(owned.Deleted && !unrelated.Deleted,"decoration ownership isolation");unrelated.Delete();
            PackCreature creature=Deployment.Creatures["test:creature"]();Check(creature.Hits==120 && creature.RawStr==100 && creature.Name=="Test beast","creature stats");
            stream=new MemoryStream();writer=new BinaryFileWriter(stream,true);creature.Serialize(writer);writer.Flush();stream.Position=0;
            var savedCreature=new PackCreature((Serial)0x1000);savedCreature.Deserialize(new BinaryFileReader(new BinaryReader(stream)));
            Check(savedCreature.Identity=="test:creature" && savedCreature.ContentLoot.Length==1 && savedCreature.ContentLoot[0].Item.Graphic==6001,"creature loot snapshot persistence");
            Deployment.Items.Clear();Deployment.Loot.Clear(); // Saved creatures must survive removal of their pack.
            savedCreature.GenerateLoot();Check(savedCreature.Backpack!=null && savedCreature.Backpack.Items.Count>=1,"creature loot generation after reload");
            savedCreature.Delete();creature.Delete();stream.Dispose();
            Console.WriteLine("PASS: all seven sections, loot bounds, item/creature/decoration persistence and ownership isolation");return 0;
        } catch(Exception e) { Console.Error.WriteLine(e);return 1; }
    }
}
