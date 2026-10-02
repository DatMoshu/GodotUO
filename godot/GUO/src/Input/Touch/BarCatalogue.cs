// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using GUO.Configuration;
using GUO.Game.Managers;

namespace GUO.Input.Touch;

/// <summary>One thing a command bar slot can do.</summary>
/// <param name="Id">Its name, as the profile saves it.</param>
/// <param name="Title">Its full name, for the slot editor's list.</param>
/// <param name="Short">Its caption on a plate: one line of font 1 at 2x (158 px).</param>
/// <param name="Category">Its group in the slot editor.</param>
/// <param name="Type">The upstream macro it runs, or None for the client's own calls.</param>
/// <param name="Sub">The macro's subtype.</param>
/// <param name="Words">What a speech slot says unless the profile says otherwise.</param>
internal sealed record BarAction(
    string Id, string Title, string Short, string Category,
    MacroType Type = MacroType.None, MacroSubType Sub = MacroSubType.MSC_NONE, string Words = null
)
{
    public bool IsSpeech => Words != null;
}

/// <summary>
/// Everything a command bar slot can hold (C10): the thirty defaults and the
/// variations and extras the hold popup offers, each one upstream's own macro
/// or the top bar's own call. Grouped for the slot editor.
/// </summary>
/// <remarks>
/// PORT DEVIATION (GUO): not in ClassicUO, whose equivalent is a macro button
/// gump per macro. Actions are saved by name; a name this build does not know
/// falls back to the slot's default.
/// </remarks>
internal static class BarCatalogue
{
    public const string Windows = "Windows", Targeting = "Targeting", Combat = "Combat", Healing = "Healing",
        Magic = "Magic and skills", Speech = "Pets and speech", Other = "Other";

    /// <summary>The editor's groups, in order.</summary>
    public static readonly string[] Categories = { Windows, Targeting, Combat, Healing, Magic, Speech, Other };

    /// <summary>
    /// Everything, by group. The client's own calls (no macro type) are the
    /// top bar's: paperdoll, backpack, journal, map, chat, options.
    /// </summary>
    public static readonly BarAction[] All =
    {
        new("paperdoll", "Paperdoll", "Paperdoll", Windows),
        new("backpack", "Backpack", "Backpack", Windows),
        new("journal", "Journal", "Journal", Windows),
        new("journalread", "Read the journal (large type)", "Read Journal", Windows),
        new("map", "Radar map", "Map", Windows),
        new("worldmap", "World map", "World Map", Windows, MacroType.Open, MacroSubType.WorldMap),
        new("status", "Status", "Status", Windows, MacroType.Open, MacroSubType.Status),
        new("skills", "Skills", "Skills", Windows, MacroType.Open, MacroSubType.Skills),
        new("spellbook", "Spellbook", "Spellbook", Windows, MacroType.Open, MacroSubType.MageSpellbook),
        new("party", "Party", "Party", Windows, MacroType.Open, MacroSubType.PartyManifest),
        new("guild", "Guild", "Guild", Windows, MacroType.Open, MacroSubType.Guild),
        new("quests", "Quest log", "Quests", Windows, MacroType.Open, MacroSubType.QuestLog),
        new("chat", "Chat (the say line)", "Chat", Windows),
        new("options", "Options", "Options", Windows),
        new("scripts", "Scripts", "Scripts", Windows),
        new("stopscript", "Stop script", "Stop script", Windows),

        new("nearest", "Nearest hostile", "Nearest Foe", Targeting, MacroType.SelectNearest, MacroSubType.Hostile),
        new("nearestparty", "Nearest party member", "Near Party", Targeting, MacroType.SelectNearest, MacroSubType.Party),
        new("nearestpet", "Nearest pet", "Near Pet", Targeting, MacroType.SelectNearest, MacroSubType.Follower),
        new("nexthostile", "Next hostile", "Next Foe", Targeting, MacroType.SelectNext, MacroSubType.Hostile),
        new("prevhostile", "Previous hostile", "Prev Foe", Targeting, MacroType.SelectPrevious, MacroSubType.Hostile),
        new("next", "Next target", "Next Target", Targeting, MacroType.TargetNext),
        new("last", "Last target", "Last Target", Targeting, MacroType.LastTarget),
        new("targetself", "Target self", "Target Self", Targeting, MacroType.TargetSelf),
        new("object", "Last object", "Last Object", Targeting, MacroType.LastObject),
        new("allnames", "All names", "All Names", Targeting, MacroType.AllNames),

        new("war", "War / Peace", "War", Combat, MacroType.WarPeace),
        new("attack", "Attack last", "Attack Last", Combat, MacroType.AttackLast),
        new("attacksel", "Attack selected target", "Attack Sel.", Combat, MacroType.AttackSelectedTarget),
        new("ability1", "Weapon ability 1", "Ability 1", Combat, MacroType.PrimaryAbility),
        new("ability2", "Weapon ability 2", "Ability 2", Combat, MacroType.SecondaryAbility),
        new("armdisarm", "Arm/disarm, right hand", "Arm/Disarm", Combat, MacroType.ArmDisarm, MacroSubType.RightHand),
        new("armleft", "Arm/disarm, left hand", "Arm Left", Combat, MacroType.ArmDisarm, MacroSubType.LeftHand),
        new("equiplast", "Equip last weapon", "Last Weapon", Combat, MacroType.EquipLastWeapon),

        new("bandage", "Bandage self", "Bandage Self", Healing, MacroType.BandageSelf),
        new("bandagetarget", "Bandage target", "Bandage Tgt", Healing, MacroType.BandageTarget),
        new("heal", "Heal potion", "Heal Potion", Healing, MacroType.UsePotion, MacroSubType.BestHealPotion),
        new("cure", "Cure potion", "Cure Potion", Healing, MacroType.UsePotion, MacroSubType.BestCurePotion),
        new("refresh", "Refresh potion", "Refresh", Healing, MacroType.UsePotion, MacroSubType.BestRefreshPotion),
        new("strength", "Strength potion", "Str Potion", Healing, MacroType.UsePotion, MacroSubType.BestStrengthPotion),
        new("agility", "Agility potion", "Agi Potion", Healing, MacroType.UsePotion, MacroSubType.BestAgiPotion),
        new("explosion", "Explosion potion", "Explosion", Healing, MacroType.UsePotion, MacroSubType.BestExplosionPotion),

        new("lastspell", "Last spell", "Last Spell", Magic, MacroType.LastSpell),
        new("lastskill", "Last skill", "Last Skill", Magic, MacroType.LastSkill),
        new("spell:heal", "Cast Heal", "Cast Heal", Magic, MacroType.CastSpell, MacroSubType.Heal),
        new("spell:greaterheal", "Cast Greater Heal", "Greater Heal", Magic, MacroType.CastSpell, MacroSubType.GreaterHeal),
        new("spell:cure", "Cast Cure", "Cast Cure", Magic, MacroType.CastSpell, MacroSubType.Cure),
        new("spell:magicarrow", "Cast Magic Arrow", "Magic Arrow", Magic, MacroType.CastSpell, MacroSubType.MagicArrow),
        new("spell:reflect", "Cast Magic Reflection", "Reflection", Magic, MacroType.CastSpell, MacroSubType.MagicReflection),
        new("spell:recall", "Cast Recall", "Recall", Magic, MacroType.CastSpell, MacroSubType.Recall),
        new("skill:hiding", "Use Hiding", "Hiding", Magic, MacroType.UseSkill, MacroSubType.Hiding),
        new("skill:meditation", "Use Meditation", "Meditate", Magic, MacroType.UseSkill, MacroSubType.Meditation),
        new("skill:detect", "Use Detecting Hidden", "Detect", Magic, MacroType.UseSkill, MacroSubType.DetectingHidden),
        new("skill:stealth", "Use Stealth", "Stealth", Magic, MacroType.UseSkill, MacroSubType.Stealth),
        new("skill:tracking", "Use Tracking", "Tracking", Magic, MacroType.UseSkill, MacroSubType.Tracking),
        new("skill:animallore", "Use Animal Lore", "Animal Lore", Magic, MacroType.UseSkill, MacroSubType.AnimalLore),

        new("follow", "Say: all follow me", "All Follow Me", Speech, Words: "all follow me"),
        new("stop", "Say: all stop", "All Stop", Speech, Words: "all stop"),
        new("allcome", "Say: all come", "All Come", Speech, Words: "all come"),
        new("allguard", "Say: all guard me", "All Guard", Speech, Words: "all guard me"),
        new("allkill", "Say: all kill", "All Kill", Speech, Words: "all kill"),
        new("bank", "Say: bank", "Bank", Speech, Words: "bank"),
        new("balance", "Say: balance", "Balance", Speech, Words: "balance"),
        new("guards", "Say: guards", "Guards", Speech, Words: "guards"),
        new("vendorbuy", "Say: vendor buy", "Vendor Buy", Speech, Words: "vendor buy"),
        new("vendorsell", "Say: vendor sell", "Vendor Sell", Speech, Words: "vendor sell"),

        new("door", "Open door", "Open Door", Other, MacroType.OpenDoor),
        new("bow", "Bow", "Bow", Other, MacroType.Bow),
        new("salute", "Salute", "Salute", Other, MacroType.Salute),
        new("zoomin", "Zoom in", "Zoom In", Other, MacroType.Zoom, MacroSubType.ZoomIn),
        new("zoomout", "Zoom out", "Zoom Out", Other, MacroType.Zoom, MacroSubType.ZoomOut),
        new("alwaysrun", "Always run on/off", "Always Run", Other, MacroType.AlwaysRun),
        new("buffs", "Buff icons on/off", "Buff Icons", Other, MacroType.ToggleBuffIconGump),
        new("closecorpses", "Close corpse windows", "Corpses", Other, MacroType.CloseCorpses),
    };

    private static readonly Dictionary<string, BarAction> _byId = new();

    static BarCatalogue()
    {
        foreach (BarAction a in All)
        {
            _byId[a.Id] = a;
        }
    }

    /// <summary>The action with this name, or null.</summary>
    public static BarAction Get(string id) => id != null && _byId.TryGetValue(id, out BarAction a) ? a : null;

    public static bool Contains(string id) => id != null && _byId.ContainsKey(id);

    /// <summary>
    /// The alternates a slot's action starts with, in its hold popup: the
    /// owner's examples (Nearest Hostile to Nearest Party and Next Hostile;
    /// Heal potion to Cure and Refresh; Attack Last to Attack Selected), and
    /// the like for the rest.
    /// </summary>
    public static (string, string) DefaultAlternates(string id) => id switch
    {
        "paperdoll" => ("status", "skills"),
        "backpack" => ("bank", "closecorpses"),
        "journal" => ("journalread", "chat"),
        "map" => ("worldmap", "zoomout"),
        "chat" => ("party", "guild"),
        "war" => ("armdisarm", "equiplast"),
        "nearest" => ("nearestparty", "nexthostile"),
        "attack" => ("attacksel", "ability1"),
        "last" => ("targetself", "next"),
        "bandage" => ("bandagetarget", "heal"),
        "next" => ("nexthostile", "prevhostile"),
        "object" => ("door", "allnames"),
        "heal" => ("cure", "refresh"),
        "cure" => ("heal", "spell:cure"),
        "ability1" => ("ability2", "armdisarm"),
        "ability2" => ("ability1", "armdisarm"),
        "lastspell" => ("spell:greaterheal", "spell:magicarrow"),
        "lastskill" => ("skill:hiding", "skill:meditation"),
        "armdisarm" => ("armleft", "equiplast"),
        "status" => ("paperdoll", "skills"),
        "skills" => ("lastskill", "skill:meditation"),
        "spellbook" => ("lastspell", "spell:recall"),
        "allnames" => ("buffs", "alwaysrun"),
        "door" => ("bow", "salute"),
        "follow" => ("allcome", "allguard"),
        "stop" => ("allcome", "allkill"),
        "bank" => ("balance", "vendorbuy"),
        "guards" => ("vendorbuy", "vendorsell"),
        "party" => ("nearestparty", "chat"),
        "options" => ("zoomin", "zoomout"),
        _ => (null, null),
    };

    /// <summary>
    /// What a speech action says: the profile's words for it (the slot editor,
    /// or Options' older four fields), else its own.
    /// </summary>
    public static string WordsFor(string id)
    {
        BarAction a = Get(id);

        if (a == null || !a.IsSpeech)
        {
            return null;
        }

        Profile p = ProfileManager.CurrentProfile;

        if (p != null && ReadWords(p.TouchBarWords).TryGetValue(id, out string saved) && !string.IsNullOrWhiteSpace(saved))
        {
            return saved;
        }

        string legacy = id switch
        {
            "follow" => p?.TouchSayFollow,
            "stop" => p?.TouchSayStop,
            "bank" => p?.TouchSayBank,
            "guards" => p?.TouchSayGuards,
            _ => null,
        };

        return string.IsNullOrWhiteSpace(legacy) ? a.Words : legacy;
    }

    /// <summary>Set a speech action's words in the profile ("" puts back its own).</summary>
    public static void SetWords(string id, string words)
    {
        Profile p = ProfileManager.CurrentProfile;

        if (p == null)
        {
            return;
        }

        Dictionary<string, string> all = ReadWords(p.TouchBarWords);

        if (string.IsNullOrWhiteSpace(words))
        {
            all.Remove(id);
        }
        else
        {
            all[id] = words.Replace("|", "").Replace("=", "").Trim();
        }

        var parts = new List<string>();

        foreach (KeyValuePair<string, string> kv in all)
        {
            parts.Add($"{kv.Key}={kv.Value}");
        }

        p.TouchBarWords = string.Join("|", parts);

        // The older fields follow, so Options shows the same words.
        switch (id)
        {
            case "follow": p.TouchSayFollow = WordsFor(id); break;
            case "stop": p.TouchSayStop = WordsFor(id); break;
            case "bank": p.TouchSayBank = WordsFor(id); break;
            case "guards": p.TouchSayGuards = WordsFor(id); break;
        }
    }

    private static Dictionary<string, string> ReadWords(string saved)
    {
        var map = new Dictionary<string, string>();

        foreach (string part in (saved ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');

            if (eq > 0)
            {
                map[part.Substring(0, eq)] = part.Substring(eq + 1);
            }
        }

        return map;
    }
}
