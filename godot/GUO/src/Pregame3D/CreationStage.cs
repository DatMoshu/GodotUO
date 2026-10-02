// SPDX-License-Identifier: BSD-2-Clause
// GUO addition, not a port: character creation for the 3D pregame. The data
// it builds is the classic gumps' (CharCreationGump, CreateCharAppearanceGump,
// CreateCharProfessionGump, CreateCharTradeGump, CreateCharSelectionCityGump):
// the same PlayerMobile, items, skills, stats, city and profession byte go to
// LoginScene.CreateCharacter. Those gumps are read, never edited.

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using GUO.Assets;
using GUO.Game;
using GUO.Game.Data;
using GUO.Game.GameObjects;
using GUO.Game.Scenes;
using GUO.Game.UI.Gumps.CharCreation;
using GUO.Input.Touch;
using GUO.Resources;
using GUO.Utility;

namespace GUO.Pregame3D;

internal sealed class CreationStage : Stage
{
    private enum Page { Appearance, Profession, Trade, City }

    private Page _page;
    private World _world;
    private PlayerMobile _character;
    private bool _female;
    private RaceType _race = RaceType.HUMAN;
    private string _name = "";
    private readonly Dictionary<Layer, int> _option = new();
    /// <summary>Per layer: the index into its hue grid, -1 for the picker's starting hue.</summary>
    private readonly Dictionary<Layer, int> _hueIndex = new();
    private ProfessionInfo _profession;
    private ProfessionInfo _category;
    private int _cityIndex;
    private int _citySelected;

    // Trade (advanced) page.
    private readonly int[] _stats = new int[3];
    private int[] _skillPick;
    private int[] _skillValue;
    private List<SkillEntry> _skillList;

    private PanelContainer _panel;
    private PanelContainer _summary;
    private Label _summaryText;
    private VBoxContainer _rows;
    private Label _title;
    private readonly List<OverlayItem> _items = new();
    private Node3D _figure;
    private Label3D _figureName;

    private static int SkillsCount => CharCreationGump._skillsCount;

    public override IEnumerable<OverlayItem> OverlayItems => _items;

    public override string Hints => _page switch
    {
        Page.Appearance => "<>  Change     A  Edit     Start  Next     B  Back",
        Page.Profession => "A  Choose     B  Back",
        Page.Trade => "<>  Change     Start  Next     B  Back",
        _ => "A  Choose city     Start  Create     B  Back",
    };

    public override void Enter()
    {
        _world = Client.Game.UO.World;
        D.Frame(D.StepPose("characters", -0.3f), null, 0.8);
        D.LidTo(1f, 0.6);

        _panel = Overlay.Card(Overlay.Parchment);
        VBoxContainer col = Overlay.Column(2);
        _title = Overlay.Text("", UoTheme.Heading);
        col.AddChild(_title);
        _rows = Overlay.Column(1);
        col.AddChild(_rows);
        _panel.AddChild(col);
        _panel.CustomMinimumSize = new Vector2(250, 0);
        D.OverlayRoot.AddChild(_panel);
        _panel.GrowHorizontal = Control.GrowDirection.Begin;
        _panel.GrowVertical = Control.GrowDirection.Both;

        _summary = Overlay.Card(Overlay.Stone);
        _summaryText = Overlay.Text("", UoTheme.Ink, wrap: true);
        _summaryText.CustomMinimumSize = new Vector2(190, 0);
        _summary.AddChild(_summaryText);
        D.OverlayRoot.AddChild(_summary);

        // A stand-in figure on a plinth in the chest (a UO sprite billboard is a later step).
        _figure = D.Scene.Plinth();
        var plinths = D.Scene.Layout.PlinthSlots;
        _figure.Transform = (plinths.Count > 0 ? plinths[plinths.Count / 2].Transform : new Placement(D.Scene.ChestInterior.Position).Transform) * _figure.Transform;
        D.Scene.Root.AddChild(_figure);
        _figureName = D.TextAbove(_figure, "", 1, new Color("e0b050"));

        // As CreateCharAppearanceGump's constructor: a human male, defaults.
        _female = false;
        _race = RaceType.HUMAN;
        _name = "";
        _hueIndex.Clear();
        ResetStyles();
        _profession = null;
        _category = null;
        _citySelected = -1;
        _skillPick = null;
        _character = null;
        Rebuild();
        ShowPage(Page.Appearance);
    }

    public override void Exit()
    {
        _panel?.QueueFree();
        _summary?.QueueFree();
        _figure?.QueueFree();
        _panel = _summary = null;
        _figure = null;
        _items.Clear();
        D.Focus.Clear();
    }

    // --- the character: CreateCharAppearanceGump's data flow ------------------------------

    private void ResetStyles()
    {
        _option[Layer.Hair] = 1;
        _option[Layer.Beard] = 0;
    }

    private bool HasBeard => !_female && _race != RaceType.ELF;

    /// <summary>A layer's hue grid as the picker builds it (ColorPickerBox: palette + 1, or 3, 8, 13 ... without one).</summary>
    private ushort[] HueGrid(Layer layer)
    {
        ushort[] palette = layer switch
        {
            Layer.Invalid => CharacterCreationValues.GetSkinPallet(_race),
            Layer.Hair or Layer.Beard => CharacterCreationValues.GetHairPallet(_race),
            _ => null,
        };

        if (palette != null)
        {
            return palette.Select(h => (ushort) (h + 1)).ToArray();
        }

        // Shirt and pants: 10 rows x 20 columns of the hues file, from Graduation 1.
        var grid = new ushort[200];
        ushort start = 2;

        for (int i = 0; i < grid.Length; i++, start += 5)
        {
            grid[i] = (ushort) (start + 1);
        }

        return grid;
    }

    /// <summary>A layer's hue: the picker's starting hue (palette[0] + 1, or 2) until one is chosen.</summary>
    private ushort Hue(Layer layer)
    {
        ushort[] grid = HueGrid(layer);
        int i = _hueIndex.TryGetValue(layer, out int v) ? v : -1;

        if (i < 0 || i >= grid.Length)
        {
            ushort[] palette = layer switch
            {
                Layer.Invalid => CharacterCreationValues.GetSkinPallet(_race),
                Layer.Hair or Layer.Beard => CharacterCreationValues.GetHairPallet(_race),
                _ => null,
            };

            return (ushort) ((palette != null && palette.Length > 0 ? palette[0] : 1) + 1);
        }

        return grid[i];
    }

    /// <summary>CreateCharAppearanceGump.CreateCharacter then UpdateEquipments, with this stage's choices.</summary>
    private void Rebuild()
    {
        if (_character == null || !_world.Mobiles.ContainsKey(_character.Serial))
        {
            _character = new PlayerMobile(_world, 1);
            _world.Mobiles.Add(_character);
        }

        LinkedObject first = _character.Items;

        while (first != null)
        {
            LinkedObject next = first.Next;
            _world.RemoveItem((Item) first, true);
            first = next;
        }

        _character.Clear();
        _character.Race = _race;
        _character.IsFemale = _female;

        if (_female)
        {
            _character.Flags |= Flags.Female;
        }
        else
        {
            _character.Flags &= ~Flags.Female;
        }

        ushort shirt = Hue(Layer.Shirt), pants = Hue(Layer.Pants);

        switch (_race)
        {
            case RaceType.GARGOYLE:
                _character.Graphic = _female ? (ushort) 0x029B : (ushort) 0x029A;
                Push(CreateItem(0x4001, shirt, Layer.Robe));
                break;

            case RaceType.ELF when _female:
                _character.Graphic = 0x025E;
                Push(CreateItem(0x1710, 0x0384, Layer.Shoes));
                Push(CreateItem(0x1531, pants, Layer.Skirt));
                Push(CreateItem(0x1518, shirt, Layer.Shirt));
                break;

            case RaceType.ELF:
                _character.Graphic = 0x025D;
                Push(CreateItem(0x1710, 0x0384, Layer.Shoes));
                Push(CreateItem(0x152F, pants, Layer.Pants));
                Push(CreateItem(0x1518, shirt, Layer.Shirt));
                break;

            default:
                _character.Graphic = _female ? (ushort) 0x0191 : (ushort) 0x0190;
                Push(CreateItem(0x1710, 0x0384, Layer.Shoes));
                Push(CreateItem(_female ? 0x1531 : 0x152F, pants, Layer.Pants));
                Push(CreateItem(0x1518, shirt, Layer.Shirt));
                break;
        }

        // UpdateEquipments.
        _character.Hue = Hue(Layer.Invalid);

        if (HasBeard)
        {
            var beards = CharacterCreationValues.GetFacialHairComboContent(_race);
            Push(CreateItem(beards.GetGraphic(Math.Clamp(_option[Layer.Beard], 0, beards.Labels.Length - 1)), Hue(Layer.Beard), Layer.Beard));
        }

        var hairs = CharacterCreationValues.GetHairComboContent(_female, _race);

        if (hairs.Labels.Length > 0)
        {
            Push(CreateItem(hairs.GetGraphic(Math.Clamp(_option[Layer.Hair], 0, hairs.Labels.Length - 1)), Hue(Layer.Hair), Layer.Hair));
        }

        _character.Name = _name;
        UpdateSummary();
    }

    private void Push(Item item)
    {
        if (item != null)
        {
            _character.PushToBack(item);
        }
    }

    /// <summary>CreateCharAppearanceGump.CreateItem: a client-side item per layer, its serial the layer.</summary>
    private Item CreateItem(int id, ushort hue, Layer layer)
    {
        Item exists = _character.FindItemByLayer(layer);

        if (exists != null)
        {
            _world.RemoveItem(exists, true);
            _character.Remove(exists);
        }

        if (id == 0)
        {
            return null;
        }

        Item item = _world.GetOrCreateItem(0x4000_0000 + (uint) layer);
        _character.Remove(item);
        item.Graphic = (ushort) id;
        item.Hue = hue;
        item.Layer = layer;
        item.Container = _character;

        return item;
    }

    private (bool elf, bool garg) AllowedRaces()
    {
        CharacterListFlags flags = _world.ClientFeatures.Flags;
        LockedFeatureFlags locks = _world.ClientLockedFeatures.Flags;

        return ((flags & CharacterListFlags.CLF_ELVEN_RACE) != 0 && locks.HasFlag(LockedFeatureFlags.ML), locks.HasFlag(LockedFeatureFlags.SA));
    }

    // --- pages ------------------------------------------------------------------------

    private void ShowPage(Page page, int focus = 0)
    {
        _page = page;

        foreach (OverlayItem item in _items)
        {
            _rows.RemoveChild(item.Control);
            item.Control.QueueFree();
        }

        _items.Clear();
        D.Focus.Clear();

        switch (page)
        {
            case Page.Appearance: AppearancePage(); break;
            case Page.Profession: ProfessionPage(); break;
            case Page.Trade: TradePage(); break;
            case Page.City: CityPage(); break;
        }

        PadFocus.LinkColumn(_items.Cast<IFocusable>().ToList());
        D.Focus.Set(_items.Count > 0 ? _items[Math.Clamp(focus, 0, _items.Count - 1)] : null);
        UpdateSummary();
        D.RefreshHints();
        Callable.From(Relayout).CallDeferred();
    }

    /// <summary>Fit both cards to what they hold now (a container grows, it never shrinks back).</summary>
    private void Relayout()
    {
        if (_panel == null)
        {
            return;
        }

        _panel.ResetSize();
        _panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterRight, Control.LayoutPresetMode.Minsize, 8);
        _summary.ResetSize();
        _summary.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft, Control.LayoutPresetMode.Minsize, 8);
    }

    private OverlayItem Add(string caption, Action activated = null, Action<int> cycle = null, Func<string> value = null, Color? ink = null)
    {
        var item = new OverlayItem(caption, activated, ink) { Cycle = cycle };

        if (value != null)
        {
            item.Value = value();

            if (cycle != null)
            {
                Action<int> inner = cycle;
                item.Cycle = d =>
                {
                    inner(d);
                    item.Value = value();
                    UpdateSummary();
                };
            }
        }

        _rows.AddChild(item.Control);
        _items.Add(item);

        return item;
    }

    private void AppearancePage()
    {
        _title.Text = "Appearance";
        var clilocs = Client.Game.UO.FileManager.Clilocs;
        (bool allowElf, bool allowGarg) = AllowedRaces();
        var races = new List<RaceType> { RaceType.HUMAN };

        if (allowElf)
        {
            races.Add(RaceType.ELF);
        }

        if (allowGarg)
        {
            races.Add(RaceType.GARGOYLE);
        }

        Add("Name", () => EditName(), value: () => _name.Length > 0 ? _name : "(choose)");
        Add("Gender", null, d =>
        {
            // HandleGenreChange: the styles reset, the hues stay.
            _female = !_female;
            ResetStyles();
            Rebuild();
            ShowPage(Page.Appearance, 1);
        }, () => _female ? "Female" : "Male");

        if (races.Count > 1)
        {
            Add("Race", null, d =>
            {
                // HandleRaceChanged: hues and styles back to their defaults.
                int i = (races.IndexOf(_race) + d + races.Count) % races.Count;
                _race = races[i];
                _hueIndex.Clear();
                ResetStyles();
                Rebuild();
                ShowPage(Page.Appearance, 2);
            }, () => _race.ToString()[0] + _race.ToString().Substring(1).ToLowerInvariant());
        }

        AddHue(clilocs.GetString(3000183) ?? "Skin", Layer.Invalid);

        var hairs = CharacterCreationValues.GetHairComboContent(_female, _race);

        if (hairs.Labels.Length > 0)
        {
            Add(clilocs.GetString(_race == RaceType.GARGOYLE ? 1112309 : 3000121) ?? "Hair", null,
                d => { _option[Layer.Hair] = (_option[Layer.Hair] + d + hairs.Labels.Length) % hairs.Labels.Length; Rebuild(); },
                () => hairs.Labels[Math.Clamp(_option[Layer.Hair], 0, hairs.Labels.Length - 1)]);
            AddHue(clilocs.GetString(_race == RaceType.GARGOYLE ? 1112322 : 3000184) ?? "Hair color", Layer.Hair);
        }

        if (HasBeard)
        {
            var beards = CharacterCreationValues.GetFacialHairComboContent(_race);

            if (beards.Labels.Length > 0)
            {
                Add(clilocs.GetString(_race == RaceType.GARGOYLE ? 1112511 : 3000122) ?? "Facial hair", null,
                    d => { _option[Layer.Beard] = (_option[Layer.Beard] + d + beards.Labels.Length) % beards.Labels.Length; Rebuild(); },
                    () => beards.Labels[Math.Clamp(_option[Layer.Beard], 0, beards.Labels.Length - 1)]);
                AddHue(clilocs.GetString(_race == RaceType.GARGOYLE ? 1112512 : 3000446) ?? "Facial hair color", Layer.Beard);
            }
        }

        AddHue(clilocs.GetString(3000440) ?? "Shirt color", Layer.Shirt);

        if (_race != RaceType.GARGOYLE)
        {
            AddHue(clilocs.GetString(3000441) ?? "Pants color", Layer.Pants);
        }

        Add("Next", NextFromAppearance, ink: UoTheme.Heading);
    }

    private void AddHue(string caption, Layer layer)
    {
        Add(caption, null, d =>
        {
            ushort[] grid = HueGrid(layer);
            int i = _hueIndex.TryGetValue(layer, out int v) ? v : -1;
            _hueIndex[layer] = ((i < 0 ? (d > 0 ? -1 : 0) : i) + d + grid.Length) % grid.Length;
            Rebuild();
        }, () => $"hue {Hue(layer)}");
    }

    private void EditName()
    {
        D.Keyboard.Open("Character name", _name, false, 16, text =>
        {
            _name = text.Trim();
            Rebuild();
            ShowPage(Page.Appearance, _items.Count - 1);
        }, () => D.RefreshHints());
        D.RefreshHints();
    }

    /// <summary>The appearance gump's Next: the name checked as it checks it.</summary>
    private void NextFromAppearance()
    {
        _character.Name = _name;
        int invalid = CreateCharAppearanceGump.Validate(_name);

        if (invalid > 0)
        {
            D.ShowMessage(Client.Game.UO.FileManager.Clilocs.GetString(invalid), EditName);
            return;
        }

        (bool allowElf, bool allowGarg) = AllowedRaces();

        if (_race == RaceType.ELF && !allowElf || _race == RaceType.GARGOYLE && !allowGarg)
        {
            return;
        }

        GUO.Utility.Logging.Log.Trace($"Creating character '{_name}'");
        ShowPage(Page.Profession);
    }

    private void ProfessionPage()
    {
        var clilocs = Client.Game.UO.FileManager.Clilocs;
        var all = Client.Game.UO.FileManager.Professions.Professions;
        List<ProfessionInfo> list;

        if (_category != null && all.TryGetValue(_category, out List<ProfessionInfo> children) && children != null)
        {
            list = children;
            _title.Text = clilocs.GetString(_category.Localization) ?? "Profession";
        }
        else
        {
            list = new List<ProfessionInfo>(all.Keys);
            _title.Text = clilocs.GetString(3000326, "Choose a Trade for Your Character");
        }

        foreach (ProfessionInfo info in list)
        {
            ProfessionInfo p = info;
            Add(clilocs.GetString(p.Localization) ?? p.Name, () => ChooseProfession(p));
        }
    }

    /// <summary>CreateCharProfessionGump.SelectProfession then CharCreationGump.SetProfession.</summary>
    private void ChooseProfession(ProfessionInfo info)
    {
        var all = Client.Game.UO.FileManager.Professions.Professions;

        if (info.Type == ProfessionLoader.PROF_TYPE.CATEGORY && all.TryGetValue(info, out List<ProfessionInfo> list) && list != null)
        {
            _category = info;
            ShowPage(Page.Profession);
            return;
        }

        // A profession chosen after another starts clean (the trade gump zeroes them too).
        foreach (Skill skill in _character.Skills)
        {
            skill.ValueFixed = 0;
            skill.BaseFixed = 0;
            skill.CapFixed = 0;
            skill.Lock = Lock.Locked;
        }

        for (int i = 0; i < SkillsCount; i++)
        {
            int skillIndex = info.SkillDefVal[i, 0];

            if (skillIndex >= _character.Skills.Length)
            {
                continue;
            }

            if ((_world.ClientFeatures.Flags & CharacterListFlags.CLF_SAMURAI_NINJA) == 0 && (skillIndex == 52 || skillIndex == 53))
            {
                for (int k = 0; k < i; k++)
                {
                    Skill skill = _character.Skills[info.SkillDefVal[k, 0]];
                    skill.ValueFixed = 0;
                    skill.BaseFixed = 0;
                    skill.CapFixed = 0;
                    skill.Lock = Lock.Locked;
                }

                D.ShowMessage(Client.Game.UO.FileManager.Clilocs.GetString(1063016));
                return;
            }

            Skill skill2 = _character.Skills[skillIndex];
            skill2.ValueFixed = (ushort) info.SkillDefVal[i, 1];
            skill2.BaseFixed = 0;
            skill2.CapFixed = 0;
            skill2.Lock = Lock.Locked;
        }

        _profession = info;
        _character.Strength = (ushort) info.StatsVal[0];
        _character.Intelligence = (ushort) info.StatsVal[1];
        _character.Dexterity = (ushort) info.StatsVal[2];

        ShowPage(info.DescriptionIndex > 0 ? Page.City : Page.Trade);
    }

    private void TradePage()
    {
        _title.Text = "Attributes and skills";
        var clilocs = Client.Game.UO.FileManager.Clilocs;
        (int[,] defSkills, int[] defStats) = ProfessionInfo.GetDefaults(Client.Game.UO.Version);

        if (_skillPick == null || _skillPick.Length != SkillsCount)
        {
            _skillPick = Enumerable.Repeat(-1, SkillsCount).ToArray();
            _skillValue = new int[SkillsCount];

            for (int i = 0; i < SkillsCount; i++)
            {
                _skillValue[i] = defSkills[i, 1];
            }

            for (int i = 0; i < 3; i++)
            {
                _stats[i] = defStats[i];
            }
        }

        _skillList = SkillChoices();
        string[] statNames = { clilocs.GetString(3000111) ?? "Strength", clilocs.GetString(3000112) ?? "Intelligence", clilocs.GetString(3000113) ?? "Dexterity" };

        for (int i = 0; i < 3; i++)
        {
            int stat = i;
            Add(statNames[i], null, d => Paired(_stats, stat, d, 10, 60), () => _stats[stat].ToString());
        }

        for (int i = 0; i < SkillsCount; i++)
        {
            int k = i;
            Add($"Skill {k + 1}", null, d =>
            {
                int n = _skillList.Count;
                _skillPick[k] = ((_skillPick[k] < 0 ? (d > 0 ? -1 : 0) : _skillPick[k]) + d + n) % n;
            }, () => _skillPick[k] < 0 ? "Click here" : _skillList[_skillPick[k]].Name);
            Add("   value", null, d => Paired(_skillValue, k, d, 0, 50), () => _skillValue[k].ToString());
        }

        Add("Next", NextFromTrade, ink: UoTheme.Heading);
    }

    /// <summary>HSliderBar's paired sliders: one up, another down, the total kept.</summary>
    private static void Paired(int[] values, int index, int delta, int min, int max)
    {
        int target = values[index] + delta;

        if (target < min || target > max)
        {
            return;
        }

        // The partner: the largest other that can give (or the smallest that can take).
        int partner = -1;

        for (int j = 0; j < values.Length; j++)
        {
            if (j == index)
            {
                continue;
            }

            bool can = delta > 0 ? values[j] - delta >= min : values[j] - delta <= max;

            if (can && (partner < 0 || (delta > 0 ? values[j] > values[partner] : values[j] < values[partner])))
            {
                partner = j;
            }
        }

        if (partner < 0)
        {
            return;
        }

        values[index] = target;
        values[partner] -= delta;
    }

    /// <summary>The skills CreateCharTradeGump offers, by the same filters.</summary>
    private List<SkillEntry> SkillChoices()
    {
        LockedFeatureFlags clientFlags = _world.ClientLockedFeatures.Flags;
        List<SkillEntry> list = Client.Game.UO.FileManager.Skills.SortedSkills
            .Where(s => s.Index != 47 && s.Index != 48 && s.Index != 54 && (_character.Race == RaceType.GARGOYLE || s.Index != 57))
            .Where(s => clientFlags.HasFlag(LockedFeatureFlags.AOS) || (s.Index != 51 && s.Index != 50 && s.Index != 49))
            .Where(s => clientFlags.HasFlag(LockedFeatureFlags.SE) || (s.Index != 52 && s.Index != 53))
            .Where(s => clientFlags.HasFlag(LockedFeatureFlags.SA) || (s.Index != 55 && s.Index != 56))
            .ToList();

        if (_character.Race == RaceType.GARGOYLE)
        {
            list.RemoveAll(s => s.Index == 31);
        }

        return list;
    }

    /// <summary>CreateCharTradeGump's Next: three (or four) different skills, then onto the character.</summary>
    private void NextFromTrade()
    {
        if (!_skillPick.All(p => p >= 0))
        {
            D.ShowMessage(Client.Game.UO.Version <= ClientVersion.CV_5090 ? ResGumps.YouMustHaveThreeUniqueSkillsChosen : Client.Game.UO.FileManager.Clilocs.GetString(1080032));
            return;
        }

        if (_skillPick.Distinct().Count() != _skillPick.Length)
        {
            D.ShowMessage(Client.Game.UO.FileManager.Clilocs.GetString(1080032));
            return;
        }

        foreach (Skill skill in _character.Skills)
        {
            skill.ValueFixed = 0;
            skill.BaseFixed = 0;
            skill.CapFixed = 0;
            skill.Lock = Lock.Locked;
        }

        for (int i = 0; i < _skillPick.Length; i++)
        {
            Skill skill = _character.Skills[_skillList[_skillPick[i]].Index];
            skill.ValueFixed = (ushort) _skillValue[i];
            skill.BaseFixed = 0;
            skill.CapFixed = 0;
            skill.Lock = Lock.Locked;
        }

        _character.Strength = (ushort) _stats[0];
        _character.Intelligence = (ushort) _stats[1];
        _character.Dexterity = (ushort) _stats[2];

        ShowPage(Page.City);
    }

    private void CityPage()
    {
        _title.Text = "Starting city";
        CityInfo[] cities = Login.Cities ?? Array.Empty<CityInfo>();

        if (cities.Length == 0)
        {
            Add("No city was sent", null);
            Add("Create character", Create, ink: UoTheme.Heading);
            return;
        }

        // CreateCharSelectionCityGump's starting city.
        CityInfo start = Client.Game.UO.Version >= ClientVersion.CV_70130 ? Login.GetCity(0) : Login.GetCity(3) ?? Login.GetCity(0);

        if (_citySelected < 0 || _citySelected >= cities.Length)
        {
            _citySelected = Math.Max(0, Array.IndexOf(cities, start));
        }

        _cityIndex = cities[_citySelected].Index;

        for (int i = 0; i < cities.Length; i++)
        {
            int index = i;
            CityInfo c = cities[i];
            OverlayItem row = Add((i == _citySelected ? "> " : "   ") + c.City, () =>
            {
                _citySelected = index;
                _cityIndex = c.Index;
                ShowPage(Page.City, index);
            });
        }

        Add("Create character", Create, ink: UoTheme.Heading);
    }

    private void Create()
    {
        if (_profession == null)
        {
            ShowPage(Page.Profession);
            return;
        }

        _character.Name = _name;
        GD.Print($"[GUO] pregame3d: create \"{_name}\" ({(_female ? "female" : "male")} {_race}), profession {_profession.DescriptionIndex}, city {_cityIndex}");
        Login.CreateCharacter(_character, _cityIndex, (byte) _profession.DescriptionIndex);
    }

    // --- commands -----------------------------------------------------------------------

    public override bool Command(PadCmd cmd)
    {
        switch (cmd)
        {
            case PadCmd.B:
                Back();
                return true;

            case PadCmd.Start:
                switch (_page)
                {
                    case Page.Appearance: NextFromAppearance(); break;
                    case Page.Trade: NextFromTrade(); break;
                    case Page.City: Create(); break;
                    default: D.Focus.Current?.Press(); break;
                }

                return true;
        }

        return false;
    }

    /// <summary>CharCreationGump.StepBack: a page back, or out to character selection from the first.</summary>
    private void Back()
    {
        switch (_page)
        {
            case Page.Appearance:
                Login.StepBack();
                break;

            case Page.Profession when _category != null:
                _category = null;
                ShowPage(Page.Profession);
                break;

            case Page.Profession:
                ShowPage(Page.Appearance);
                break;

            case Page.Trade:
                ShowPage(Page.Profession);
                break;

            case Page.City:
                ShowPage(_profession != null && _profession.DescriptionIndex > 0 ? Page.Profession : Page.Trade);
                break;
        }
    }

    private void UpdateSummary()
    {
        if (_summaryText == null || _character == null)
        {
            return;
        }

        var clilocs = Client.Game.UO.FileManager.Clilocs;
        string profession = _profession == null ? "-" : clilocs.GetString(_profession.Localization) ?? _profession.Name;
        string skills = string.Join(", ", _character.Skills.Where(s => s.ValueFixed > 0).OrderByDescending(s => s.ValueFixed).Take(SkillsCount).Select(s => $"{s.Name} {s.ValueFixed}"));
        string city = Login.Cities != null && _citySelected >= 0 && _citySelected < Login.Cities.Length ? Login.Cities[_citySelected].City : "-";

        _summaryText.Text = $"{(_name.Length > 0 ? _name : "(no name)")}\n"
            + $"{(_female ? "Female" : "Male")} {_race.ToString().ToLowerInvariant()}, body 0x{_character.Graphic:X4}, skin {_character.Hue}\n"
            + $"Profession: {profession}\n"
            + $"Str {_character.Strength}  Int {_character.Intelligence}  Dex {_character.Dexterity}\n"
            + $"Skills: {(skills.Length > 0 ? skills : "-")}\n"
            + $"City: {city}";

        if (_figureName != null)
        {
            _figureName.Text = _name.Length > 0 ? _name : "?";
        }

        Callable.From(Relayout).CallDeferred();
    }
}
