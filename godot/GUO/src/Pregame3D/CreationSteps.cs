// SPDX-License-Identifier: BSD-2-Clause
// GUO addition, not a port: the five steps of the Tailor's Table (see CreationStage.cs).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using GUO.Assets;
using GUO.Game.Data;
using GUO.Game.Scenes;
using GUO.Input.Touch;
using GUO.Resources;
using GUO.Utility;

namespace GUO.Pregame3D;

internal sealed partial class CreationStage
{
    private static readonly Random Rng = new();

    // ==========================================================================
    // 1 Trade: profession cards and the focused one's details
    // ==========================================================================

    private Label _detailName;
    private VBoxContainer _detailRows;

    private List<ProfessionInfo> TradeList()
    {
        var all = Client.Game.UO.FileManager.Professions.Professions;

        if (_category != null && all.TryGetValue(_category, out List<ProfessionInfo> children) && children != null)
        {
            return children;
        }

        // The gump's list: the loader's top level, in its order.
        return new List<ProfessionInfo>(all.Keys);
    }

    private void TradeStep()
    {
        var clilocs = Client.Game.UO.FileManager.Clilocs;
        _body.AddChild(Text(_category != null ? Title(clilocs.GetString(_category.Localization) ?? _category.Name) : clilocs.GetString(3000326, "Choose a Trade for Your Character"), Heading));

        var grid = new GridContainer { Columns = 4, MouseFilter = Control.MouseFilterEnum.Ignore };
        grid.AddThemeConstantOverride("h_separation", 4);
        grid.AddThemeConstantOverride("v_separation", 4);
        _body.AddChild(grid);

        List<ProfessionInfo> list = TradeList();
        var cards = new List<IFocusable>();

        foreach (ProfessionInfo info in list)
        {
            ProfessionInfo p = info;
            string name = Title(clilocs.GetString(p.Localization) ?? p.Name);
            PanelContainer card = Card(margin: 4);
            Texture2D icon = p.Graphic != 0 ? PregameAssets.Texture(p.Graphic) : null;
            card.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            VBoxContainer col = Overlay.Column(0);
            card.AddChild(col);

            if (icon != null)
            {
                // The profession's icon inside the card, above its name: its
                // own size when it fits, else shrunk to the card's icon row.
                float iconRow = Math.Min(icon.GetHeight(), Math.Max(20f, (D.OverlayRoot.Size.Y - 34 - 28) * 0.13f));
                col.AddChild(new TextureRect
                {
                    Texture = icon,
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                    TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
                    CustomMinimumSize = new Vector2(0, iconRow),
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                });
            }

            Label caption = Text(name, Ink);
            caption.HorizontalAlignment = HorizontalAlignment.Center;
            col.AddChild(caption);
            grid.AddChild(card);

            bool chosen = _profession == p;
            var focus = new UiFocus(card, null, () => ChooseProfession(p)) { Tag = "prof:" + name.ToLowerInvariant() };
            focus.Shown = on =>
            {
                card.AddThemeStyleboxOverride("panel", Parch(4, on ? 1 : chosen ? 2 : 0));
                caption.AddThemeColorOverride("font_color", on ? Active : Ink);

                if (on)
                {
                    ShowDetail(p);
                }
            };
            focus.Shown(false);
            _items.Add(focus);
            cards.Add(focus);
        }

        PadFocus.LinkGrid(cards, 4);

        PanelContainer detail = Card(6);
        detail.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        VBoxContainer d = Overlay.Column(2);
        detail.AddChild(d);
        _detailName = Text("", Heading);
        d.AddChild(_detailName);
        _detailRows = Overlay.Column(1);
        d.AddChild(_detailRows);
        _body.AddChild(detail);

        if (_profession != null && list.Contains(_profession))
        {
            int i = list.IndexOf(_profession);
            Callable.From(() => D.Focus.Set(cards[i])).CallDeferred();
        }
    }

    private void ShowDetail(ProfessionInfo p)
    {
        if (_detailRows == null)
        {
            return;
        }

        var clilocs = Client.Game.UO.FileManager.Clilocs;
        _detailName.Text = Title(clilocs.GetString(p.Localization) ?? p.Name);

        foreach (Node c in _detailRows.GetChildren())
        {
            c.QueueFree();
        }

        var all = Client.Game.UO.FileManager.Professions.Professions;

        if (p.Type == ProfessionLoader.PROF_TYPE.CATEGORY && all.TryGetValue(p, out List<ProfessionInfo> list) && list != null)
        {
            _detailRows.AddChild(Text($"{list.Count} trades: " + string.Join(", ", list.Select(x => Title(clilocs.GetString(x.Localization) ?? x.Name))), Ink, wrap: true));
            return;
        }

        if (p.DescriptionIndex <= 0)
        {
            _detailRows.AddChild(Text("Choose your own attributes and skills.", Ink, wrap: true));
            return;
        }

        // StatsVal is Str, Int, Dex; shown Str, Dex, Int as the status bar does.
        _detailRows.AddChild(StatBar("Str", p.StatsVal[0], 60));
        _detailRows.AddChild(StatBar("Dex", p.StatsVal[2], 60));
        _detailRows.AddChild(StatBar("Int", p.StatsVal[1], 60));

        var skills = Client.Game.UO.FileManager.Skills.Skills;
        var names = new List<string>();

        for (int i = 0; i < SkillsCount; i++)
        {
            int index = p.SkillDefVal[i, 0];

            if (index >= 0 && index < skills.Count && p.SkillDefVal[i, 1] > 0)
            {
                names.Add($"{skills[index].Name} {p.SkillDefVal[i, 1]}");
            }
        }

        // One wrapped line: the card stays inside the screen at 360 lines.
        _detailRows.AddChild(Text(string.Join(",  ", names), Ink, wrap: true));
    }

    /// <summary>A labelled bar: the value of a maximum, with its number.</summary>
    private static Control StatBar(string label, int value, int max, bool focused = false)
    {
        HBoxContainer row = Overlay.Row(6);
        Label l = Text(label, focused ? Active : Ink);
        l.CustomMinimumSize = new Vector2(30, 0);
        row.AddChild(l);
        var back = new Panel { CustomMinimumSize = new Vector2(150, 8), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, MouseFilter = Control.MouseFilterEnum.Ignore };
        back.AddThemeStyleboxOverride("panel", Box(new Color("3a2c1c"), Border, 1, 0));
        var fill = new ColorRect { Color = focused ? Gold : new Color("a8742c"), MouseFilter = Control.MouseFilterEnum.Ignore, Position = new Vector2(1, 1), Size = new Vector2(Math.Max(0, 148f * value / max), 6) };
        back.AddChild(fill);
        row.AddChild(back);
        row.AddChild(Text(value.ToString(), focused ? Active : Ink));
        return row;
    }

    /// <summary>CreateCharProfessionGump.SelectProfession then CharCreationGump.SetProfession.</summary>
    private void ChooseProfession(ProfessionInfo info)
    {
        var all = Client.Game.UO.FileManager.Professions.Professions;

        if (info.Type == ProfessionLoader.PROF_TYPE.CATEGORY && all.TryGetValue(info, out List<ProfessionInfo> list) && list != null)
        {
            _category = info;
            ShowStep(Step.Trade);
            return;
        }

        // SetProfession refuses Samurai and Ninja skills to a shard without them.
        if (info.DescriptionIndex > 0 && (_world.ClientFeatures.Flags & CharacterListFlags.CLF_SAMURAI_NINJA) == 0)
        {
            for (int i = 0; i < SkillsCount; i++)
            {
                if (info.SkillDefVal[i, 0] is 52 or 53)
                {
                    D.ShowMessage(Client.Game.UO.FileManager.Clilocs.GetString(1063016));
                    return;
                }
            }
        }

        bool changed = _profession != info;
        _profession = info;

        if (Advanced && (changed || _skillPick == null))
        {
            (int[,] defSkills, int[] defStats) = ProfessionInfo.GetDefaults(Client.Game.UO.Version);
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

        _done.Add(Step.Trade);
        ApplyTrade();
        ShowStep(Step.Look);
    }

    private void RandomTrade()
    {
        List<ProfessionInfo> list = TradeList().Where(p => p.Type != ProfessionLoader.PROF_TYPE.CATEGORY).ToList();

        if (list.Count > 0)
        {
            ChooseProfession(list[Rng.Next(list.Count)]);
        }
    }

    // ==========================================================================
    // 2 Look: rows that cycle, colour swatches, the palette
    // ==========================================================================

    private void LookStep()
    {
        var clilocs = Client.Game.UO.FileManager.Clilocs;
        List<RaceType> races = Races();

        LookRow("body", "Body", () => _female ? "Female" : "Male", d =>
        {
            // HandleGenreChange: the styles reset, the hues stay.
            _female = !_female;
            ResetStyles();
            Rebuild();
        });

        if (races.Count > 1)
        {
            LookRow("race", "Race", () => Title(_race.ToString()), d =>
            {
                // HandleRaceChanged: hues and styles back to their defaults.
                _race = races[(races.IndexOf(_race) + d + races.Count) % races.Count];
                _hueIndex.Clear();
                ResetStyles();
                Rebuild();
                ShowStep(Step.Look, "race");
            });
        }

        ColourRow("skin", clilocs.GetString(3000183) ?? "Skin", Layer.Invalid);

        var hairs = CharacterCreationValues.GetHairComboContent(_female, _race);

        if (hairs.Labels.Length > 0)
        {
            LookRow("hair", clilocs.GetString(_race == RaceType.GARGOYLE ? 1112309 : 3000121) ?? "Hair",
                () => hairs.Labels[Math.Clamp(_option[Layer.Hair], 0, hairs.Labels.Length - 1)],
                d => { _option[Layer.Hair] = (_option[Layer.Hair] + d + hairs.Labels.Length) % hairs.Labels.Length; Rebuild(); });
            ColourRow("haircolour", clilocs.GetString(_race == RaceType.GARGOYLE ? 1112322 : 3000184) ?? "Hair colour", Layer.Hair);
        }

        if (HasBeard)
        {
            var beards = CharacterCreationValues.GetFacialHairComboContent(_race);

            if (beards.Labels.Length > 0)
            {
                LookRow("beard", clilocs.GetString(_race == RaceType.GARGOYLE ? 1112511 : 3000122) ?? "Beard",
                    () => beards.Labels[Math.Clamp(_option[Layer.Beard], 0, beards.Labels.Length - 1)],
                    d => { _option[Layer.Beard] = (_option[Layer.Beard] + d + beards.Labels.Length) % beards.Labels.Length; Rebuild(); });
                ColourRow("beardcolour", clilocs.GetString(_race == RaceType.GARGOYLE ? 1112512 : 3000446) ?? "Beard colour", Layer.Beard);
            }
        }

        ColourRow("shirt", clilocs.GetString(3000440) ?? "Shirt", Layer.Shirt);

        if (_race != RaceType.GARGOYLE)
        {
            ColourRow("pants", clilocs.GetString(3000441) ?? "Pants", Layer.Pants);
        }

        PadFocus.LinkColumn(_items.Cast<IFocusable>().ToList());
    }

    private PanelContainer RowShell(string caption, out HBoxContainer value, out Label captionLabel)
    {
        PanelContainer row = Card(margin: 3);
        HBoxContainer h = Overlay.Row(6);
        row.AddChild(h);
        captionLabel = Text(caption, Ink);
        captionLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        h.AddChild(captionLabel);
        value = Overlay.Row(6);
        h.AddChild(value);
        _body.AddChild(row);
        return row;
    }

    private void Lit(PanelContainer row, Label caption, bool on)
    {
        row.AddThemeStyleboxOverride("panel", Parch(3, on ? 1 : 0));
        caption.AddThemeColorOverride("font_color", on ? Active : Ink);
    }

    private void LookRow(string tag, string caption, Func<string> value, Action<int> cycle)
    {
        PanelContainer row = RowShell(caption, out HBoxContainer v, out Label cap);
        Label left = Text("<", Heading), text = Text(value(), Ink), right = Text(">", Heading);
        text.CustomMinimumSize = new Vector2(110, 0);
        text.HorizontalAlignment = HorizontalAlignment.Center;
        v.AddChild(left);
        v.AddChild(text);
        v.AddChild(right);
        var f = new UiFocus(row) { Tag = tag };
        f.Shown = on => Lit(row, cap, on);
        f.Cycle = d =>
        {
            cycle(d);
            text.Text = value();
            RefreshTabs();
        };
        f.Shown(false);
        _items.Add(f);
    }

    private void ColourRow(string tag, string caption, Layer layer)
    {
        PanelContainer row = RowShell(caption, out HBoxContainer v, out Label cap);
        var swatch = new ColorRect { CustomMinimumSize = new Vector2(48, 12), Color = Mannequin.Swatch(Hue(layer)), MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        var frame = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        frame.AddThemeStyleboxOverride("panel", Box(Ink, Ink, 1, 1));
        frame.AddChild(swatch);
        v.AddChild(Text("<", Heading));
        v.AddChild(frame);
        v.AddChild(Text(">", Heading));
        var f = new UiFocus(row) { Tag = tag };
        f.Shown = on => Lit(row, cap, on);
        f.Cycle = d =>
        {
            ushort[] grid = HueGrid(layer);
            int i = _hueIndex.TryGetValue(layer, out int x) ? x : -1;
            _hueIndex[layer] = ((i < 0 ? (d > 0 ? -1 : 0) : i) + d + grid.Length) % grid.Length;
            Rebuild();
            swatch.Color = Mannequin.Swatch(Hue(layer));
        };
        f.Pressed = () => OpenPalette(layer, caption, () => swatch.Color = Mannequin.Swatch(Hue(layer)));
        f.Shown(false);
        _items.Add(f);
    }

    /// <summary>The hue set the classic gump offers for the slot, as a grid; the figure follows the focus.</summary>
    private void OpenPalette(Layer layer, string caption, Action refresh)
    {
        ushort[] grid = HueGrid(layer);
        int columns = HueColumns(layer);
        int before = _hueIndex.TryGetValue(layer, out int b) ? b : -1;
        VBoxContainer col = Overlay.Column(4);
        col.AddChild(Text(caption, Heading));
        var gridC = new GridContainer { Columns = columns, MouseFilter = Control.MouseFilterEnum.Ignore };
        gridC.AddThemeConstantOverride("h_separation", 1);
        gridC.AddThemeConstantOverride("v_separation", 1);
        col.AddChild(gridC);
        int cell = columns > 12 ? 10 : 16;
        var items = new List<IOverlayFocusable>();

        for (int i = 0; i < grid.Length; i++)
        {
            int index = i;
            var holder = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            holder.AddThemeStyleboxOverride("panel", Box(Ink, Ink, 1, 1));
            holder.AddChild(new ColorRect { Color = Mannequin.Swatch(grid[i]), CustomMinimumSize = new Vector2(cell, cell), MouseFilter = Control.MouseFilterEnum.Ignore });
            gridC.AddChild(holder);
            var f = new UiFocus(holder) { Tag = $"hue:{index}" };
            f.Shown = on =>
            {
                holder.AddThemeStyleboxOverride("panel", on ? Box(Gold, Gold, 2, 2) : Box(Ink, Ink, 1, 1));

                if (on)
                {
                    // A live look while browsing.
                    _hueIndex[layer] = index;
                    Rebuild();
                }
            };
            f.Pressed = () => ClosePopover(true);
            f.Shown(false);
            items.Add(f);
        }

        PadFocus.LinkGrid(items.Cast<IFocusable>().ToList(), columns);
        _beforePopover = D.Focus.Current;
        OpenPopover(col, items, items[Math.Clamp(before, 0, items.Count - 1)], keep =>
        {
            if (!keep)
            {
                if (before < 0)
                {
                    _hueIndex.Remove(layer);
                }
                else
                {
                    _hueIndex[layer] = before;
                }

                Rebuild();
            }

            refresh();
        });
    }

    private void RandomLook()
    {
        var hairs = CharacterCreationValues.GetHairComboContent(_female, _race);

        if (hairs.Labels.Length > 0)
        {
            _option[Layer.Hair] = Rng.Next(hairs.Labels.Length);
        }

        if (HasBeard)
        {
            var beards = CharacterCreationValues.GetFacialHairComboContent(_race);

            if (beards.Labels.Length > 0)
            {
                _option[Layer.Beard] = Rng.Next(beards.Labels.Length);
            }
        }

        foreach (Layer layer in new[] { Layer.Invalid, Layer.Hair, Layer.Beard, Layer.Shirt, Layer.Pants })
        {
            _hueIndex[layer] = Rng.Next(HueGrid(layer).Length);
        }

        Rebuild();
        ShowStep(Step.Look, FocusTag);
    }

    // ==========================================================================
    // 3 Skills (Advanced): fixed-total attributes, skill slots, the skill list
    // ==========================================================================

    private void SkillsStep()
    {
        var clilocs = Client.Game.UO.FileManager.Clilocs;
        _skillList = SkillChoices();
        _body.AddChild(Text("Attributes", Heading));

        // The trade gump's order is Str, Int, Dex; shown Str, Dex, Int.
        (int index, string name)[] stats = { (0, clilocs.GetString(3000111) ?? "Strength"), (2, clilocs.GetString(3000113) ?? "Dexterity"), (1, clilocs.GetString(3000112) ?? "Intelligence") };

        foreach ((int index, string name) in stats)
        {
            int stat = index;
            string label = name.Length > 3 ? name.Substring(0, 3) : name;
            PanelContainer row = Card(margin: 3);
            _body.AddChild(row);
            Control bar = StatBar(label, _stats[stat], 60);
            row.AddChild(bar);
            var f = new UiFocus(row) { Tag = "stat:" + label.ToLowerInvariant() };
            f.Shown = on =>
            {
                row.AddThemeStyleboxOverride("panel", Parch(3, on ? 1 : 0));
                Redraw(row, StatBar(label, _stats[stat], 60, on));
            };
            f.Cycle = d =>
            {
                Paired(_stats, stat, d, 10, 60);
                ApplyTrade();
                RefreshStatRows();
            };
            f.Shown(false);
            _items.Add(f);
        }

        _body.AddChild(Text($"Skills (total {_skillValue.Sum()})", Heading));

        for (int i = 0; i < SkillsCount; i++)
        {
            int slot = i;
            PanelContainer row = RowShell("", out HBoxContainer v, out Label cap);
            cap.Text = SkillName(slot);
            Label value = Text($"<  {_skillValue[slot]}  >", Heading);
            v.AddChild(value);
            var f = new UiFocus(row) { Tag = $"slot:{slot}" };
            f.Shown = on => Lit(row, cap, on);
            f.Cycle = d =>
            {
                Paired(_skillValue, slot, d, 0, 50);
                ApplyTrade();
                RefreshSkillRows();
            };
            f.Pressed = () => OpenSkillList(slot);
            f.Shown(false);
            _items.Add(f);
        }

        PadFocus.LinkColumn(_items.Cast<IFocusable>().ToList());
    }

    private static void Redraw(PanelContainer row, Control content)
    {
        foreach (Node c in row.GetChildren())
        {
            row.RemoveChild(c);
            c.QueueFree();
        }

        row.AddChild(content);
    }

    private void RefreshStatRows()
    {
        foreach (IOverlayFocusable f in _items)
        {
            if (f is UiFocus u && (u.Tag as string)?.StartsWith("stat:") == true)
            {
                u.Shown(ReferenceEquals(D.Focus.Current, u));
            }
        }
    }

    private void RefreshSkillRows()
    {
        // Values and names, in place (paired sliders move the others too).
        string tag = FocusTag;
        ShowStep(Step.Skills, tag);
    }

    private string SkillName(int slot) => _skillPick[slot] < 0 ? "Choose a skill" : _skillList[_skillPick[slot]].Name;

    private void ClearFocusedSkill()
    {
        if (FocusTag is string tag && tag.StartsWith("slot:") && int.TryParse(tag.Substring(5), out int slot))
        {
            _skillPick[slot] = -1;
            ApplyTrade();
            RefreshSkillRows();
        }
    }

    /// <summary>Every skill the gump offers, under the client's default skill groups.</summary>
    private void OpenSkillList(int slot)
    {
        var groups = new (string name, int[] skills)[]
        {
            (ResGeneral.Miscellaneous, new[] { 4, 6, 10, 12, 19, 3, 36 }),
            (ResGeneral.Combat, new[] { 1, 31, 42, 17, 41, 5, 40, 27, 57, 43, 50, 51, 52, 53 }),
            (ResGeneral.TradeSkills, new[] { 0, 7, 8, 11, 13, 23, 44, 45, 34, 37 }),
            (ResGeneral.Magic, new[] { 16, 56, 25, 46, 55, 26, 54, 32, 49 }),
            (ResGeneral.Wilderness, new[] { 2, 35, 18, 20, 38, 39 }),
            (ResGeneral.Thieving, new[] { 14, 21, 24, 30, 48, 28, 33, 47 }),
            (ResGeneral.Bard, new[] { 15, 29, 9, 22 }),
        };

        var offered = new Dictionary<int, int>(); // skill index -> position in _skillList

        for (int i = 0; i < _skillList.Count; i++)
        {
            offered[_skillList[i].Index] = i;
        }

        var scroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(260, Math.Max(120, D.OverlayRoot.Size.Y - 110)),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        VBoxContainer list = Overlay.Column(1);
        list.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        scroll.AddChild(list);
        var items = new List<IOverlayFocusable>();
        var placed = new HashSet<int>();
        IOverlayFocusable current = null;

        void Group(string name, IEnumerable<int> skills)
        {
            var present = skills.Where(s => offered.ContainsKey(s) && placed.Add(s)).ToList();

            if (present.Count == 0)
            {
                return;
            }

            list.AddChild(Text(name, Heading));

            foreach (int s in present)
            {
                int pos = offered[s];
                var row = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
                Label l = Text("   " + _skillList[pos].Name, Ink);
                row.AddChild(l);
                list.AddChild(row);
                bool taken = _skillPick.Contains(pos) && _skillPick[slot] != pos;
                var f = new UiFocus(row) { Tag = "skill:" + _skillList[pos].Name.ToLowerInvariant() };
                f.Shown = on =>
                {
                    row.AddThemeStyleboxOverride("panel", on ? Box(Gold, Gold, 0, 1) : Box(new Color(0, 0, 0, 0), new Color(0, 0, 0, 0), 0, 1));
                    l.AddThemeColorOverride("font_color", on ? Active : taken ? Muted : Ink);

                    if (on)
                    {
                        Callable.From(() => { if (IsInstanceValid(scroll)) scroll.EnsureControlVisible(row); }).CallDeferred();
                    }
                };
                f.Pressed = () =>
                {
                    _skillPick[slot] = pos;
                    ClosePopover(true);
                };
                f.Shown(false);
                items.Add(f);

                if (_skillPick[slot] == pos)
                {
                    current = f;
                }
            }
        }

        foreach ((string name, int[] skills) in groups)
        {
            Group(name, skills);
        }

        Group("Other", _skillList.Select(s => s.Index));

        PadFocus.LinkColumn(items.Cast<IFocusable>().ToList());
        VBoxContainer col = Overlay.Column(4);
        col.AddChild(Text($"Skill {slot + 1}", Heading));
        col.AddChild(scroll);
        _beforePopover = D.Focus.Current;
        OpenPopover(col, items, current, keep =>
        {
            ApplyTrade();
            Callable.From(RefreshSkillRows).CallDeferred();
        });
    }

    /// <summary>HSliderBar's paired sliders: one up, another down, the total kept.</summary>
    private static void Paired(int[] values, int index, int delta, int min, int max)
    {
        int target = values[index] + delta;

        if (target < min || target > max)
        {
            return;
        }

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
            .Where(s => s.Index != 47 && s.Index != 48 && s.Index != 54 && (_race == RaceType.GARGOYLE || s.Index != 57))
            .Where(s => clientFlags.HasFlag(LockedFeatureFlags.AOS) || (s.Index != 51 && s.Index != 50 && s.Index != 49))
            .Where(s => clientFlags.HasFlag(LockedFeatureFlags.SE) || (s.Index != 52 && s.Index != 53))
            .Where(s => clientFlags.HasFlag(LockedFeatureFlags.SA) || (s.Index != 55 && s.Index != 56))
            .ToList();

        if (_race == RaceType.GARGOYLE)
        {
            list.RemoveAll(s => s.Index == 31);
        }

        return list;
    }

    /// <summary>CreateCharTradeGump.ValidateValues.</summary>
    private bool SkillsValid()
    {
        if (_skillPick == null || !_skillPick.All(p => p >= 0))
        {
            D.ShowMessage(Client.Game.UO.Version <= ClientVersion.CV_5090 ? ResGumps.YouMustHaveThreeUniqueSkillsChosen : Client.Game.UO.FileManager.Clilocs.GetString(1080032));
            return false;
        }

        if (_skillPick.Distinct().Count() != _skillPick.Length)
        {
            D.ShowMessage(Client.Game.UO.FileManager.Clilocs.GetString(1080032));
            return false;
        }

        return true;
    }

    // ==========================================================================
    // 4 Home: the map, a pin per city
    // ==========================================================================

    private bool _mapReady;
    private TextureRect _mapRect;
    private ImageTexture _mapTexture;
    private Label _cityName, _cityText;

    private CityInfo[] Cities => Login.Cities ?? Array.Empty<CityInfo>();

    private CityInfo City()
    {
        CityInfo[] cities = Cities;

        if (cities.Length == 0)
        {
            return null;
        }

        if (_citySelected < 0 || _citySelected >= cities.Length)
        {
            // CreateCharSelectionCityGump's starting city.
            CityInfo start = Client.Game.UO.Version >= ClientVersion.CV_70130 ? Login.GetCity(0) : Login.GetCity(3) ?? Login.GetCity(0);
            _citySelected = Math.Max(0, Array.IndexOf(cities, start));
        }

        return cities[_citySelected];
    }

    private string CityName() => City()?.City;

    private void HomeStep()
    {
        CityInfo[] cities = Cities;
        City();

        MapPlan plan = PlanMap(cities, D.OverlayRoot.Size);
        float width = plan.Width, height = plan.Height;
        int lines = plan.Lines;
        var map = new Control { CustomMinimumSize = new Vector2(width, height), MouseFilter = Control.MouseFilterEnum.Ignore, ClipContents = true };
        var mapFrame = Card(4, Overlay.Stone);
        mapFrame.AddChild(map);
        _body.AddChild(mapFrame);
        _mapRect = new TextureRect { TextureFilter = CanvasItem.TextureFilterEnum.Nearest, StretchMode = TextureRect.StretchModeEnum.Scale, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, MouseFilter = Control.MouseFilterEnum.Ignore, Size = new Vector2(width, height) };
        map.AddChild(_mapRect);

        PanelContainer info = Card(6);
        info.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        VBoxContainer col = Overlay.Column(2);
        info.AddChild(col);
        _cityName = Text("", Heading);
        _cityText = Text("", Ink, wrap: true);
        _cityText.MaxLinesVisible = lines;
        _cityText.CustomMinimumSize = new Vector2(width - 16, 0);
        col.AddChild(_cityName);
        col.AddChild(_cityText);
        _body.AddChild(info);

        if (cities.Length == 0)
        {
            _cityName.Text = "The shard sent no cities.";
            return;
        }

        bool real = plan.Real;
        Rect2I region = plan.Region;
        float dispW = plan.Shown.Size.X, dispH = plan.Shown.Size.Y;
        Vector2 dispAt = plan.Shown.Position;
        _mapRect.Position = dispAt;
        _mapRect.Size = plan.Shown.Size;

        if (real)
        {
            // Started after the login (PrefetchMap) with this same plan; if it
            // is not done yet, a line in the panel says so until it is.
            Label drawing = Text("Drawing the map...", Cream);
            drawing.Position = new Vector2(10, 8);
            map.AddChild(drawing);
            Task<Image> task = PregameAssets.Map(plan.Facet, region, new Vector2I((int) dispW, (int) dispH));
            _mapReady = false;

            void Show(Image img)
            {
                if (img != null && IsInstanceValid(_mapRect))
                {
                    _mapTexture = ImageTexture.CreateFromImage(img);
                    _mapRect.Texture = _mapTexture;
                }

                if (IsInstanceValid(drawing))
                {
                    drawing.Visible = false;
                }

                _mapReady = true;
            }

            if (task.IsCompleted)
            {
                Show(task.Result);
            }
            else
            {
                task.ContinueWith(t => Callable.From(() => Show(t.Result)).CallDeferred());
            }
        }
        else
        {
            map.AddChild(new ColorRect { Color = new Color("c9b98e"), Size = new Vector2(width, height), MouseFilter = Control.MouseFilterEnum.Ignore });
            _mapReady = true;
        }

        var pins = new List<IFocusable>();
        var at = new List<Vector2>();
        int oldMaxX = Math.Max(1, (int) cities.Max(c => c.X)), oldMaxY = Math.Max(1, (int) cities.Max(c => c.Y));

        for (int i = 0; i < cities.Length; i++)
        {
            int index = i;
            CityInfo c = cities[i];
            Vector2 p = real
                ? dispAt + new Vector2((c.X - region.Position.X) / (float) region.Size.X * dispW, (c.Y - region.Position.Y) / (float) region.Size.Y * dispH)
                : new Vector2(c.X / (float) oldMaxX * (width - 20) + 10, c.Y / (float) oldMaxY * (height - 20) + 10);

            var pin = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore };
            var label = Text(c.City, Cream);
            label.AddThemeConstantOverride("outline_size", 4);
            label.AddThemeColorOverride("font_outline_color", Ink);
            label.Visible = false;
            map.AddChild(pin);
            map.AddChild(label);
            bool chosen = i == _citySelected;

            void Place(bool on)
            {
                int s = on ? 9 : 6;
                pin.Size = new Vector2(s, s);
                pin.Position = p - new Vector2(s / 2f, s / 2f);
                pin.AddThemeStyleboxOverride("panel", Box(on ? Gold : chosen ? Active : new Color("f0e6c8"), Ink, 1, 0));
                label.Visible = on || chosen;
                label.AddThemeColorOverride("font_color", on ? Gold : Cream);
                label.Position = new Vector2(Math.Clamp(p.X + 7, 0, width - 70), Math.Clamp(p.Y - 18, 0, height - 18));
            }

            var f = new UiFocus(pin) { Tag = "city:" + c.City.ToLowerInvariant() };
            f.Shown = on =>
            {
                Place(on);

                if (on)
                {
                    _cityName.Text = c.City + (string.IsNullOrWhiteSpace(c.Building) ? "" : "  -  " + c.Building);
                    _cityText.Text = PlainText(c.Description);
                }
            };
            f.Pressed = () =>
            {
                _citySelected = index;
                _done.Add(Step.Home);
                ShowStep(Step.Name);
            };
            f.Shown(false);
            _items.Add(f);
            pins.Add(f);
            at.Add(p);
        }

        PadFocus.LinkByPositions(pins, at);
        int sel = _citySelected;
        Callable.From(() => { if (sel >= 0 && sel < pins.Count) D.Focus.Set(pins[sel]); }).CallDeferred();
    }

    private readonly record struct MapPlan(bool Real, int Facet, Rect2I Region, float Width, float Height, Rect2 Shown, int Lines);

    /// <summary>
    /// The Home map's size in the card (from the overlay's room) and the
    /// region of the facet it shows: the cities with a margin, never past the
    /// mainland's edge, letterboxed at its own aspect.
    /// </summary>
    private static MapPlan PlanMap(CityInfo[] cities, Vector2 room)
    {
        float width = Math.Max(200, room.X * 0.62f - 8 - 20);
        // The city's text gets three lines where there is room (two on a short screen); the map the rest.
        int lines = room.Y >= 400 ? 3 : 2;
        float lineHeight = UoTheme.Font.GetHeight(UoTheme.FontSize) + 8;
        float height = Math.Min(width * 0.62f, Math.Max(90, room.Y - 34 - 28 - 20 - 24 - 36 - (lines + 1) * lineHeight));

        if (cities.Length == 0)
        {
            return new MapPlan(false, 0, default, width, height, new Rect2(0, 0, width, height), lines);
        }

        // The cities' real places on their facet (the 7.0.13+ list carries
        // them); an older list has only the old gump's points, so no map.
        bool real = cities.All(c => c.IsNewCity);
        int facet = (int) cities[0].Map;
        int minX = cities.Min(c => c.X), maxX = cities.Max(c => c.X), minY = cities.Min(c => c.Y), maxY = cities.Max(c => c.Y);
        float spanX = Math.Max(200, maxX - minX), spanY = Math.Max(200, maxY - minY);
        float aspect = width / height;
        float padX = spanX * 0.12f + 60, padY = spanY * 0.12f + 60;
        float rw = spanX + padX * 2, rh = spanY + padY * 2;

        if (rw / rh > aspect)
        {
            rh = rw / aspect;
        }
        else
        {
            rw = rh * aspect;
        }

        // Britannia's mainland on the first two facets ends at x 5120 (the lost lands lie east).
        int facetWidth = facet <= 1 ? 5120 : Client.Game.UO.FileManager.Maps.MapsDefaultSize[Math.Clamp(facet, 0, 5), 0];
        int facetHeight = Client.Game.UO.FileManager.Maps.MapsDefaultSize[Math.Clamp(facet, 0, 5), 1];
        rw = Math.Min(rw, facetWidth);
        rh = Math.Min(rh, facetHeight);
        float dispW = (int) Math.Min(width, height * rw / rh), dispH = (int) (dispW * rh / rw);
        var dispAt = new Vector2((int) ((width - dispW) / 2f), (int) ((height - dispH) / 2f));
        float rx = Math.Clamp((minX + maxX) / 2f - rw / 2f, 0, Math.Max(0, facetWidth - rw));
        float ry = Math.Clamp((minY + maxY) / 2f - rh / 2f, 0, Math.Max(0, facetHeight - rh));

        return new MapPlan(real, facet, new Rect2I((int) rx, (int) ry, (int) rw, (int) rh), width, height, new Rect2(dispAt, dispW, dispH), lines);
    }

    /// <summary>After the login, as soon as the cities are known: the Home map, drawn in the background.</summary>
    public static void PrefetchMap(CityInfo[] cities, Vector2 room)
    {
        MapPlan plan = PlanMap(cities, room);

        if (plan.Real)
        {
            PregameAssets.Map(plan.Facet, plan.Region, new Vector2I((int) plan.Shown.Size.X, (int) plan.Shown.Size.Y));
        }
    }

    /// <summary>A city's description without its HTML (the clilocs carry h2 and br).</summary>
    private static string PlainText(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return "";
        }

        string s = System.Text.RegularExpressions.Regex.Replace(html, "<h2>.*?</h2>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, "<br\\s*/?>", " ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, "<[^>]+>", "");
        return System.Text.RegularExpressions.Regex.Replace(s.Replace("\n", " "), "\\s+", " ").Trim();
    }

    // ==========================================================================
    // 5 Name: the field, a summary, Enter Britannia
    // ==========================================================================

    private void NameStep()
    {
        var clilocs = Client.Game.UO.FileManager.Clilocs;

        PanelContainer field = _nameField = Card(8);
        Label name = Text(_name.Length > 0 ? _name : "(press A to name your character)", _name.Length > 0 ? Ink : Muted, _name.Length > 0 ? 2 : 1);
        name.CustomMinimumSize = new Vector2(0, 34);
        name.VerticalAlignment = VerticalAlignment.Center;
        field.AddChild(name);
        _nameLabel = name;
        _body.AddChild(field);
        var f = new UiFocus(field) { Tag = "name" };
        f.Shown = on => field.AddThemeStyleboxOverride("panel", Parch(8, on ? 1 : 0));
        f.Pressed = EditName;
        f.Shown(false);
        _items.Add(f);

        PanelContainer summary = Card(6);
        summary.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        VBoxContainer s = Overlay.Column(1);
        summary.AddChild(s);
        string trade = _profession == null ? "-" : Title(clilocs.GetString(_profession.Localization) ?? _profession.Name);
        ApplyTrade();
        s.AddChild(Text($"Trade: {trade}", Ink));
        s.AddChild(Text($"Str {_character.Strength}   Dex {_character.Dexterity}   Int {_character.Intelligence}", Ink));
        s.AddChild(Text("Skills: " + string.Join(", ", _character.Skills.Where(x => x.ValueFixed > 0).OrderByDescending(x => x.ValueFixed).Select(x => $"{x.Name} {x.ValueFixed}")), Ink, wrap: true));
        s.AddChild(Text($"Home: {CityName() ?? "-"}", Ink));
        _body.AddChild(summary);

        PanelContainer enter = Card(8);
        enter.AddThemeStyleboxOverride("panel", Parch(8, 2));
        Label enterText = Text("Enter Britannia", Active, 2);
        enterText.HorizontalAlignment = HorizontalAlignment.Center;
        enter.AddChild(enterText);
        _body.AddChild(enter);
        var e = new UiFocus(enter) { Tag = "enter" };
        e.Shown = on => enter.AddThemeStyleboxOverride("panel", Parch(8, on ? 1 : 2));
        e.Pressed = Next;
        e.Shown(false);
        _items.Add(e);

        PadFocus.LinkColumn(_items.Cast<IFocusable>().ToList());
    }

    private PanelContainer _nameField;
    private Label _nameLabel;

    public static Control ProbeNameField => (PregameScreen.Instance?.Stage as CreationStage)?._nameField;

    private void EditName()
    {
        // Typed at the name's own size, in the row.
        _nameLabel?.AddThemeFontSizeOverride("font_size", UoTheme.FontSize * 2);
        D.Keyboard.Open("Character name", _name, false, 16, text =>
        {
            _name = text.Trim();
            _character.Name = _name;
            ShowStep(Step.Name, _name.Length > 0 ? "enter" : "name");
        }, () => ShowStep(Step.Name, "name"), field: _nameField, fieldText: _nameLabel);
        D.RefreshHints();
    }

    /// <summary>Typing on the Name step opens the keyboard with that letter.</summary>
    public override bool Key(InputEventKey k)
    {
        if (_step == Step.Name && _popover == null && FocusTag == "name" && k.Unicode >= 32 && !k.CtrlPressed && !k.AltPressed)
        {
            EditName();
            D.Keyboard.Key(k);
            return true;
        }

        return false;
    }
}
