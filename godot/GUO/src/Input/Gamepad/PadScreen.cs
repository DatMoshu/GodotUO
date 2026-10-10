// SPDX-License-Identifier: BSD-2-Clause
// GUO addition, not a port: upstream ClassicUO has no gamepad.

using System;
using System.Collections.Generic;
using Godot;
using GUO.Compat;
using GUO.Configuration;
using GUO.Game;
using GUO.Game.Data;
using GUO.Game.GameObjects;
using GUO.Game.Managers;
using GUO.Game.UI.Gumps;
using GUO.Input.Touch;
using GUO.Pregame3D;
using GUO.Utility;
using GUO.Utility.Collections;
using GUO.Assets;

namespace GUO.Input.Gamepad
{
    /// <summary>
    /// A menu-wheel window as a controller screen (Steam Deck, 1280x800, no
    /// mouse). D-pad / left stick moves, A acts, B closes, LB/RB change page.
    /// The backpack is the client's open-pack gump (0x003C) with the side
    /// straps cropped off, then sliced into a 9-slice that fills the right
    /// half of the client as a square-ish satchel (body + open flap). That
    /// rectangle does not follow the debug UI scale: LB+R3 only changes how
    /// many grid squares fit in the pocket. The bag sits over the live world:
    /// the world keeps rendering under the right half, and the gump's
    /// transparent texels show it. Nothing is painted behind the bag. Items
    /// sit in the pocket with a scrollbar; icons use DisplayedGraphic at
    /// whole-number nearest scale (never fractional). The focused item's details sit under
    /// the grid in the bag's lower band, in cream ink. Button hints sit on
    /// the left half, right-aligned against the bag's left edge. The
    /// paperdoll still uses its own gump, aspect-kept.
    /// Other windows stay a stone-framed list. While any screen is up the world
    /// camera is centred on the left half (Diablo-style) and still drawn
    /// under the screen.
    /// </summary>
    internal static class PadScreen
    {
        private sealed class Line
        {
            public string Label;
            public string Detail;
            public ushort Art;
            public uint Serial;
            public bool Wearable;
            public Action Act;
            public Action Drop;
            public Action Equip;
            public Action Context;
        }

        private static readonly string[] Maps =
        {
            "Felucca", "Trammel", "Ilshenar", "Malas", "Tokuno", "Ter Mur",
        };

        private static readonly Layer[] Worn =
        {
            Layer.OneHanded, Layer.TwoHanded, Layer.Helmet, Layer.Earrings, Layer.Necklace,
            Layer.Ring, Layer.Bracelet, Layer.Torso, Layer.Arms, Layer.Gloves, Layer.Tunic,
            Layer.Shirt, Layer.Robe, Layer.Cloak, Layer.Waist, Layer.Pants, Layer.Skirt,
            Layer.Legs, Layer.Shoes, Layer.Talisman, Layer.Mount,
        };

        private const ushort PackGump = 0x003C;   // open backpack container art
        private const ushort DollGump = 0x07d0;   // player paperdoll base
        private const int PackCols = 4;
        private const int DollCols = 3;
        private const float StickRepeat = 0.22f;
        // Classic container.def for 0x003C: Left, Top, Right, Bottom in art pixels.
        // (ContainerData stores Right/Bottom in Width/Height; see ContainerGump.)
        private const float PackLeft = 44f, PackTop = 65f, PackRight = 186f, PackBottom = 159f;
        private const float Cell = 80f;
        private const float CellGap = 4f;
        // Scrollbar thickness in window pixels. Divided by UI scale when the
        // grid is laid out in art pixels, so the bar does not grow with LB+R3.
        private const float ScrollScreen = 14f;
        // The world keeps the left half. The bag is the right half, one fixed
        // screen rectangle. UI scale does not change either share.
        internal const float WorldShare = 0.5f;

        private static Control _root;
        private static Panel _frame;
        private static Control _stage;
        private static TextureRect _hero;
        private static Panel _bag;
        private static GridContainer _grid;
        private static Rect2 _bagInStage;
        private static VBoxContainer _list;
        private static Control _hints;
        private static Label _title, _pageLabel, _detail;
        private static TextureRect _icon;
        private static Panel _scrollTrack, _scrollThumb;
        private static int _focus, _page, _pages = 1, _top;
        private static int _sig = int.MinValue;
        private static int _cols = 1;
        private static int _rowsVisible = 1;
        private static float _cell = Cell;
        private static float _stickWait;
        private static int _stickDx, _stickDy;
        private static readonly List<Line> _lines = new();
        private static string _hintKey;
        private static int _gridStamp = int.MinValue;
        private static int _cutX, _cutW, _cutH;
        private static int _sliceKey = int.MinValue;

        /// <summary>Whole-number scale of the menu panel's content (1–3). Frame stays edge-anchored.</summary>
        public static int PanelScale { get; private set; } = 1;

        /// <summary>Font scale for menu panels (backpack, paperdoll, skills, …), 1–3.</summary>
        public static int MenuFontScale { get; private set; } = 1;

        /// <summary>Font scale for journal/chat text only, 1–3 — independent of menu fonts.</summary>
        public static int ChatFontScale { get; private set; } = 1;

        /// <summary>Backpack/paperdoll cells on screen right now (columns × visible rows).</summary>
        public static int VisibleCapacity =>
            IsArtPanel ? Math.Max(1, _rowsVisible) * Math.Max(1, _cols) : 0;

        /// <summary>The detail line sits under the grid, on the bag, inside the panel.</summary>
        public static bool DetailUnderPack
        {
            get
            {
                if (_detail == null || _stage == null || _grid == null
                    || !GodotObject.IsInstanceValid(_detail) || !_detail.Visible
                    || _detail.GetParent() != _stage)
                {
                    return false;
                }

                float drawnH = _detail.Size.Y * Math.Max(0.01f, _detail.Scale.Y);
                float gridBottom = _grid.Position.Y + _grid.Size.Y;

                return _detail.Position.Y + 2f >= gridBottom
                    && _detail.Position.Y + drawnH <= _stage.Size.Y + 2f
                    && _detail.Size.Y >= 16f;
            }
        }

        /// <summary>
        /// Button hints sit on the left half, right-aligned against the bag's
        /// left edge (or the panel), not in the bottom-right corner and not on
        /// the far left of the world strip.
        /// </summary>
        public static bool HintsOnBag
        {
            get
            {
                if (_hints == null || _root == null || !GodotObject.IsInstanceValid(_hints) || !_hints.Visible)
                {
                    return false;
                }

                float drawnW = _hints.Size.X * Math.Max(0.01f, _hints.Scale.X);
                float drawnH = _hints.Size.Y * Math.Max(0.01f, _hints.Scale.Y);
                float right = _hints.Position.X + drawnW;
                float bagLeft = _frame != null
                    ? _frame.Position.X + (_content?.Position.X ?? 0f) + _bagInStage.Position.X
                    : _root.Size.X * WorldShare;
                float target = _bagInStage.Size.X > 8f ? bagLeft : (_frame?.Position.X ?? bagLeft);

                return right <= _root.Size.X * WorldShare + 12f
                    && Math.Abs(right - target) <= 12f
                    && _hints.Position.X >= 2f
                    && _hints.Position.Y >= -1f
                    && _hints.Position.Y + drawnH <= _root.Size.Y + 2f;
            }
        }

        private static Control _content;
        private static Vector2 _frameSize;

        private static bool _cut;
        private static Point _savedPos, _savedSize;
        private static bool _savedFull;

        public static bool IsOpen { get; private set; }

        public static WheelWindow? Current { get; private set; }

        public static int RowCount => _lines.Count;

        public static int Focus => _focus;

        /// <summary>Focused row label (probe evidence: pick a non-gold item).</summary>
        public static string FocusLabel =>
            _focus >= 0 && _focus < _lines.Count ? (_lines[_focus].Label ?? "") : "";

        public static ushort FocusArt =>
            _focus >= 0 && _focus < _lines.Count ? _lines[_focus].Art : (ushort)0;

        public static int Columns => _cols;

        /// <summary>The screen covers the client's area (world left, menu right).</summary>
        public static bool FillsClient
        {
            get
            {
                if (!IsOpen || _root == null || !GodotObject.IsInstanceValid(_root) || !_root.Visible)
                {
                    return false;
                }

                Rect2 area = PadOverlay.ClientRect;

                return _root.Size.X >= area.Size.X - 2f && _root.Size.Y >= area.Size.Y - 2f && _root.Size.X > 64f;
            }
        }

        /// <summary>True while the world camera is centred on the left half.</summary>
        public static bool HalfCut => _cut;

        /// <summary>
        /// The world is drawn under the screen as well: it runs from the left
        /// edge to the right edge of the window, so the bag's transparent
        /// texels show the map, and its centre is the middle of the left half.
        /// </summary>
        public static bool WorldUnderScreen
        {
            get
            {
                var camera = Client.Game?.Scene?.Camera;

                if (!_cut || camera == null || Client.Game == null)
                {
                    return false;
                }

                Rectangle window = Client.Game.ClientBounds;
                int centre = camera.Bounds.X + camera.Bounds.Width / 2;

                return camera.Bounds.X <= 0
                    && camera.Bounds.X + camera.Bounds.Width >= window.Width - 1
                    && camera.Bounds.Height >= window.Height - 1
                    && centre > 0 && centre < window.Width * WorldShare;
            }
        }

        public static void Open(WheelWindow w)
        {
            World world = Client.Game?.UO?.World;

            if (world == null || !world.InGame || world.Player == null)
            {
                return;
            }

            if (PadWizard.IsOpen)
            {
                return;
            }

            IsOpen = true;
            Current = w;
            _page = 0;
            _focus = 0;
            _top = 0;
            _sig = int.MinValue;
            _stickWait = 0f;
            _stickDx = _stickDy = 0;
            _hintKey = null;
            _gridStamp = int.MinValue;
            _sliceKey = int.MinValue;
            Ask(world, w);
            ApplyHalfCut();
            Show();
            Tick();
            GD.Print($"[GUO] pad screen: {w}");
        }

        public static void Close()
        {
            if (!IsOpen)
            {
                return;
            }

            IsOpen = false;
            Current = null;
            _lines.Clear();
            RestoreCamera();

            if (_root != null && GodotObject.IsInstanceValid(_root))
            {
                _root.Visible = false;
            }
        }

        /// <summary>D-pad / stick: move in the list or the grid.</summary>
        public static void Move(int dx, int dy)
        {
            if (!IsOpen || _lines.Count == 0)
            {
                return;
            }

            if (dx == 0 && dy == 0)
            {
                return;
            }

            if (_cols > 1)
            {
                int cols = _cols;
                int rows = Math.Max(1, (_lines.Count + cols - 1) / cols);
                int x = _focus % cols;
                int y = _focus / cols;
                x = (x + dx + cols) % cols;
                y = (y + dy + rows) % rows;
                int next = y * cols + x;

                if (next >= _lines.Count)
                {
                    next = Math.Min(_lines.Count - 1, y * cols + (_lines.Count - 1) % cols);
                }

                _focus = next;
                EnsureGridVisible();
            }
            else
            {
                int d = dy != 0 ? dy : dx;
                _focus = (_focus + d + _lines.Count) % _lines.Count;
            }

            Paint();
        }

        /// <summary>Legacy 1D step used by older call sites.</summary>
        public static void Move(int d) => Move(0, d);

        /// <summary>Left stick while the screen is up: walk the focus with a repeat.</summary>
        public static void Steer(float lx, float ly, float dt)
        {
            if (!IsOpen || _lines.Count == 0)
            {
                return;
            }

            const float dead = 0.55f;
            int dx = lx < -dead ? -1 : lx > dead ? 1 : 0;
            int dy = ly < -dead ? -1 : ly > dead ? 1 : 0;

            if (dx == 0 && dy == 0)
            {
                _stickWait = 0f;
                _stickDx = _stickDy = 0;

                return;
            }

            if (dx != _stickDx || dy != _stickDy)
            {
                _stickDx = dx;
                _stickDy = dy;
                _stickWait = 0f;
                Move(dx, dy);

                return;
            }

            _stickWait += dt;

            if (_stickWait >= StickRepeat)
            {
                _stickWait = 0f;
                Move(dx, dy);
            }
        }

        /// <summary>LB / RB.</summary>
        public static void Page(int d)
        {
            if (!IsOpen || _pages < 2)
            {
                return;
            }

            _page = (_page + d + _pages) % _pages;
            _focus = 0;
            _top = 0;
            _sig = int.MinValue;
            Tick();
        }

        public static void Activate()
        {
            if (!IsOpen || _focus < 0 || _focus >= _lines.Count)
            {
                return;
            }

            _lines[_focus].Act?.Invoke();
            _sig = int.MinValue;
        }

        /// <summary>X: drop the focused item (or put a worn one in the pack).</summary>
        public static void Drop()
        {
            if (!IsOpen || _focus < 0 || _focus >= _lines.Count)
            {
                return;
            }

            _lines[_focus].Drop?.Invoke();
            _sig = int.MinValue;
        }

        /// <summary>Y: equip the focused pack item, or use a worn one.</summary>
        public static void Equip()
        {
            if (!IsOpen || _focus < 0 || _focus >= _lines.Count)
            {
                return;
            }

            Line line = _lines[_focus];
            (line.Equip ?? line.Act)?.Invoke();
            _sig = int.MinValue;
        }

        /// <summary>Back: the client's context menu for the focused thing.</summary>
        public static void Context()
        {
            if (!IsOpen || _focus < 0 || _focus >= _lines.Count)
            {
                return;
            }

            _lines[_focus].Context?.Invoke();
        }

        /// <summary>True when this screen shows item quick-actions (pack / doll).</summary>
        public static bool HasItemActions =>
            Current is WheelWindow.Backpack or WheelWindow.Paperdoll;

        public static void CyclePanelScale() => PanelScale = PanelScale >= 3 ? 1 : PanelScale + 1;

        public static void CycleMenuFont() => MenuFontScale = MenuFontScale >= 3 ? 1 : MenuFontScale + 1;

        public static void CycleChatFont() => ChatFontScale = ChatFontScale >= 3 ? 1 : ChatFontScale + 1;

        /// <summary>Once a frame while it is up: refill when the world changed, keep the half-cut and layout.</summary>
        public static void Tick()
        {
            if (!IsOpen)
            {
                return;
            }

            if (_root == null || !GodotObject.IsInstanceValid(_root))
            {
                Show();
            }

            if (_root == null)
            {
                return;
            }

            if (!_cut || !CameraHeld())
            {
                ApplyHalfCut();
            }

            int sig = Signature();

            if (sig != _sig)
            {
                _sig = sig;
                Rebuild();
                Paint();
            }

            Layout();
        }

        // --- what the client already knows -------------------------------------------------------

        private static void Ask(World world, WheelWindow w)
        {
            switch (w)
            {
                case WheelWindow.Backpack:
                    GameActions.OpenBackpack(world);
                    break;
                case WheelWindow.Paperdoll:
                    GameActions.OpenPaperdoll(world, world.Player);
                    break;
                case WheelWindow.Skills:
                    GameActions.OpenSkills(world);
                    break;
                case WheelWindow.Spellbook:
                    PadWheel.RunMacro(world, MacroType.Open, MacroSubType.MageSpellbook);
                    break;
            }
        }

        private static void Rebuild()
        {
            _lines.Clear();
            _pages = 1;
            _cols = 1;
            World world = Client.Game?.UO?.World;
            PlayerMobile player = world?.Player;

            if (player == null || Current is not WheelWindow w)
            {
                return;
            }

            switch (w)
            {
                case WheelWindow.Backpack: Pack(player); break;
                case WheelWindow.Paperdoll: Doll(player); break;
                case WheelWindow.Journal: Journal(); break;
                case WheelWindow.Skills: Skills(player); break;
                case WheelWindow.Spellbook: Spells(); break;
                case WheelWindow.WorldMap: Map(world, player); break;
                case WheelWindow.Macros: Macros(world); break;
                case WheelWindow.Options: Options(world); break;
                case WheelWindow.Status: Status(world, player); break;
                case WheelWindow.Party: Party(world); break;
            }

            if (_lines.Count == 0)
            {
                _lines.Add(new Line { Label = "Empty" });
            }

            _focus = Math.Clamp(_focus, 0, _lines.Count - 1);
        }



        private static string LayerLabel(Layer layer) => layer switch
        {
            Layer.OneHanded => "Right hand",
            Layer.TwoHanded => "Left hand",
            Layer.Helmet => "Head",
            Layer.Earrings => "Earrings",
            Layer.Necklace => "Necklace",
            Layer.Ring => "Ring",
            Layer.Bracelet => "Bracelet",
            Layer.Torso => "Chest armour",
            Layer.Arms => "Arms",
            Layer.Gloves => "Gloves",
            Layer.Tunic => "Tunic",
            Layer.Shirt => "Shirt",
            Layer.Robe => "Robe",
            Layer.Cloak => "Cloak",
            Layer.Waist => "Waist",
            Layer.Pants => "Pants",
            Layer.Skirt => "Skirt",
            Layer.Legs => "Leg armour",
            Layer.Shoes => "Shoes",
            Layer.Talisman => "Talisman",
            Layer.Mount => "Mount",
            _ => layer.ToString(),
        };

        /// <summary>Tiledata names carry %s% plural markers; strip them for the pad UI.</summary>
        private static string ItemLabel(Item item)
        {
            if (item == null)
            {
                return "Item";
            }

            string raw = !string.IsNullOrEmpty(item.Name) ? item.Name : item.ItemData.Name;
            string cleaned = StringHelper.GetPluralAdjustedString(raw ?? "", item.Amount > 1);
            cleaned = string.IsNullOrWhiteSpace(cleaned) ? "Item" : cleaned.Trim();
            return StringHelper.CapitalizeAllWords(cleaned);
        }

        private static void Pack(PlayerMobile player)
        {
            _cols = PackCols;
            Item pack = player.FindItemByLayer(Layer.Backpack);

            if (pack == null)
            {
                return;
            }

            for (LinkedObject o = pack.Items; o != null; o = o.Next)
            {
                if (o is not Item item || item.IsDestroyed)
                {
                    continue;
                }

                Item held = item;
                string name = ItemLabel(item);
                bool wear = item.ItemData.IsWearable;
                _lines.Add(new Line
                {
                    Label = name,
                    Detail = item.Amount > 1 ? item.Amount.ToString() : (wear ? "equip" : ""),
                    Art = item.DisplayedGraphic,
                    Serial = item.Serial,
                    Wearable = wear,
                    Act = () => GameActions.DoubleClick(Client.Game.UO.World, held.Serial),
                    Drop = () => DropHeld(held),
                    Equip = wear ? () => EquipHeld(held) : null,
                    Context = () => GameActions.OpenPopupMenu(held.Serial, true),
                });
            }

            // Container-slot order. Do not sort.
        }

        private static void Doll(PlayerMobile player)
        {
            _cols = DollCols;

            foreach (Layer layer in Worn)
            {
                Item item = player.FindItemByLayer(layer);

                if (item == null || item.IsDestroyed)
                {
                    _lines.Add(new Line { Label = LayerLabel(layer), Detail = "empty" });
                    continue;
                }

                Item held = item;
                string name = ItemLabel(item);
                _lines.Add(new Line
                {
                    Label = name,
                    Detail = LayerLabel(layer),
                    Art = item.DisplayedGraphic,
                    Serial = item.Serial,
                    Wearable = true,
                    Act = () => GameActions.DoubleClick(Client.Game.UO.World, held.Serial),
                    Drop = () => UnequipToPack(held),
                    Equip = () => GameActions.DoubleClick(Client.Game.UO.World, held.Serial),
                    Context = () => GameActions.OpenPopupMenu(held.Serial, true),
                });
            }
        }

        private static void DropHeld(Item item)
        {
            World world = Client.Game?.UO?.World;
            PlayerMobile player = world?.Player;

            if (world == null || player == null || item == null || item.IsDestroyed)
            {
                return;
            }

            if (!GameActions.PickUp(world, item.Serial, 0, 0, item.Amount))
            {
                return;
            }

            GameActions.DropItem(item.Serial, player.X, player.Y, player.Z, 0);
        }

        private static void EquipHeld(Item item)
        {
            World world = Client.Game?.UO?.World;

            if (world == null || item == null || item.IsDestroyed || !item.ItemData.IsWearable)
            {
                return;
            }

            if (!GameActions.PickUp(world, item.Serial, 0, 0, item.Amount))
            {
                return;
            }

            GameActions.Equip(world);
        }

        private static void UnequipToPack(Item item)
        {
            World world = Client.Game?.UO?.World;
            PlayerMobile player = world?.Player;
            Item pack = player?.FindItemByLayer(Layer.Backpack);

            if (world == null || pack == null || item == null || item.IsDestroyed)
            {
                return;
            }

            if (!GameActions.PickUp(world, item.Serial, 0, 0, item.Amount))
            {
                return;
            }

            GameActions.DropItem(item.Serial, 0xFFFF, 0xFFFF, 0, pack.Serial);
        }

        private static void Journal()
        {
            Deque<JournalEntry> entries = JournalManager.Entries;
            int start = Math.Max(0, entries.Count - 80);

            for (int i = entries.Count - 1; i >= start; i--)
            {
                JournalEntry e = entries[i];
                _lines.Add(new Line
                {
                    Label = string.IsNullOrEmpty(e.Name) ? e.Text : e.Name + ": " + e.Text,
                    Detail = e.Time.ToString("HH:mm"),
                });
            }
        }

        private static void Skills(PlayerMobile player)
        {
            _pages = 2;
            bool known = _page == 0;

            foreach (Skill skill in player.Skills)
            {
                if (skill == null || string.IsNullOrEmpty(skill.Name))
                {
                    continue;
                }

                if (known && skill.ValueFixed <= 0 && skill.BaseFixed <= 0)
                {
                    continue;
                }

                Skill held = skill;
                _lines.Add(new Line
                {
                    Label = skill.Name,
                    Detail = $"Value {skill.Value:0.0}",
                    Act = skill.IsClickable ? () => GameActions.UseSkill(held.Index) : null,
                });
            }
        }

        private static void Spells()
        {
            var pages = new List<(string name, IEnumerable<SpellDefinition> spells)>();

            for (int circle = 0; circle < 8; circle++)
            {
                int first = circle * 8 + 1;
                var list = new List<SpellDefinition>();

                for (int id = first; id < first + 8; id++)
                {
                    if (SpellsMagery.GetAllSpells.TryGetValue(id, out SpellDefinition spell))
                    {
                        list.Add(spell);
                    }
                }

                pages.Add(($"Circle {circle + 1}", list));
            }

            pages.Add(("Necromancy", SpellsNecromancy.GetAllSpells.Values));
            pages.Add(("Chivalry", SpellsChivalry.GetAllSpells.Values));
            pages.Add(("Bushido", SpellsBushido.GetAllSpells.Values));
            pages.Add(("Ninjitsu", SpellsNinjitsu.GetAllSpells.Values));
            pages.Add(("Spellweaving", SpellsSpellweaving.GetAllSpells.Values));
            pages.Add(("Mysticism", SpellsMysticism.GetAllSpells.Values));
            _pages = pages.Count;
            _page = Math.Clamp(_page, 0, _pages - 1);

            foreach (SpellDefinition spell in pages[_page].spells)
            {
                if (spell == null || string.IsNullOrEmpty(spell.Name))
                {
                    continue;
                }

                SpellDefinition held = spell;
                _lines.Add(new Line
                {
                    Label = spell.Name,
                    Detail = spell.ManaCost > 0 ? spell.ManaCost.ToString() : "",
                    Act = () => GameActions.CastSpell(held.ID),
                });
            }
        }

        private static void Map(World world, PlayerMobile player)
        {
            string facet = world.MapIndex >= 0 && world.MapIndex < Maps.Length ? Maps[world.MapIndex] : world.MapIndex.ToString();
            _lines.Add(new Line { Label = player.Name ?? "You", Detail = $"{facet}  {player.X}, {player.Y}, {player.Z}" });

            foreach (Mobile mobile in world.Mobiles.Values)
            {
                if (mobile == null || mobile == player || mobile.IsDestroyed || mobile.Distance > 24)
                {
                    continue;
                }

                Mobile held = mobile;
                _lines.Add(new Line
                {
                    Label = string.IsNullOrEmpty(mobile.Name) ? "Someone" : mobile.Name,
                    Detail = $"{mobile.Distance} {Dir(player, mobile)}",
                    Act = () => GameActions.DoubleClick(world, held.Serial),
                    Context = () => GameActions.OpenPopupMenu(held.Serial, true),
                });
            }

            _lines.Sort((a, b) =>
            {
                if (a.Act == null) return -1;
                if (b.Act == null) return 1;
                return string.Compare(a.Detail, b.Detail, StringComparison.Ordinal);
            });
        }

        private static void Macros(World world)
        {
            foreach (Macro macro in world.Macros.GetAllMacros())
            {
                if (macro?.Items is not MacroObject first)
                {
                    continue;
                }

                MacroObject held = first;
                _lines.Add(new Line
                {
                    Label = string.IsNullOrEmpty(macro.Name) ? "Macro" : macro.Name,
                    Act = () =>
                    {
                        world.Macros.SetMacroToExecute(held);
                        Close();
                    },
                });
            }
        }

        private static void Options(World world)
        {
            _lines.Add(new Line
            {
                Label = "Set controls",
                Detail = "A",
                Act = () =>
                {
                    Close();
                    PadWizard.Open();
                },
            });
            _lines.Add(new Line
            {
                Label = "Radar",
                Detail = PadBindings.RadarRange.ToString(),
                Act = () =>
                {
                    int n = PadBindings.RadarRange;
                    PadBindings.RadarRange = n >= 24 ? 6 : n + 2;
                },
            });
            _lines.Add(new Line
            {
                Label = "Always run",
                Detail = ProfileManager.CurrentProfile != null && ProfileManager.CurrentProfile.AlwaysRun ? "On" : "Off",
                Act = () => PadWheel.RunMacro(world, MacroType.AlwaysRun),
            });
            _lines.Add(new Line
            {
                Label = "Options window",
                Act = () =>
                {
                    Close();
                    GameActions.OpenSettings(world);
                },
            });
            _lines.Add(new Line
            {
                Label = "Menu scale",
                Detail = PanelScale.ToString(),
                Act = () => { CyclePanelScale(); },
            });
            _lines.Add(new Line
            {
                Label = "Menu font",
                Detail = MenuFontScale.ToString(),
                Act = () => { CycleMenuFont(); },
            });
            _lines.Add(new Line
            {
                Label = "Journal font",
                Detail = ChatFontScale.ToString(),
                Act = () => { CycleChatFont(); },
            });
        }

        private static void Status(World world, PlayerMobile player)
        {
            // Vitals only — coordinates belong on World map, not Status.
            string who = string.IsNullOrEmpty(player.Name) ? "You" : player.Name;
            Add("Name", who);
            Add("Hits", $"{player.Hits} / {player.HitsMax}");
            Add("Mana", $"{player.Mana} / {player.ManaMax}");
            Add("Stamina", $"{player.Stamina} / {player.StaminaMax}");
            Add("Strength", player.Strength.ToString());
            Add("Dexterity", player.Dexterity.ToString());
            Add("Intelligence", player.Intelligence.ToString());
            Add("Weight", $"{player.Weight} / {player.WeightMax}");
            Add("Gold", player.Gold.ToString());
            Add("Followers", $"{player.Followers} / {player.FollowersMax}");
            Add("Luck", player.Luck.ToString());
        }

        private static void Party(World world)
        {
            PartyMember[] members = world.Party?.Members;

            if (members == null)
            {
                return;
            }

            foreach (PartyMember member in members)
            {
                if (member == null || !SerialHelper.IsValid(member.Serial))
                {
                    continue;
                }

                Mobile mobile = world.Mobiles.Get(member.Serial);
                _lines.Add(new Line
                {
                    Label = member.Name ?? "Member",
                    Detail = mobile != null ? $"{mobile.Hits}/{mobile.HitsMax}" : "",
                });
            }
        }

        private static void Add(string label, string detail) => _lines.Add(new Line { Label = label, Detail = detail });

        private static string Dir(Mobile from, Mobile to)
        {
            int dx = to.X - from.X, dy = to.Y - from.Y;
            string x = dx > 1 ? "E" : dx < -1 ? "W" : "";
            string y = dy > 1 ? "S" : dy < -1 ? "N" : "";

            return (y + x).Length == 0 ? "here" : y + x;
        }

        private static int Signature()
        {
            int h = ((int)(Current ?? 0) * 397) ^ (_page * 17) ^ _lines.Count ^ (_cols << 9) ^ (PanelScale << 11) ^ (MenuFontScale << 13) ^ (ChatFontScale << 15);
            h ^= (int)MathF.Round(PadOverlay.UiScale * 100f) << 17;
            World world = Client.Game?.UO?.World;
            PlayerMobile player = world?.Player;

            if (player == null)
            {
                return h;
            }

            h = (h * 397) ^ player.Hits ^ (player.Mana << 8) ^ player.X ^ (player.Y << 4);
            Item pack = player.FindItemByLayer(Layer.Backpack);

            if (pack != null)
            {
                for (LinkedObject o = pack.Items; o != null; o = o.Next)
                {
                    if (o is Item item)
                    {
                        h = (h * 31) ^ (int)item.Serial ^ item.Amount;
                    }
                }
            }

            foreach (Layer layer in Worn)
            {
                Item item = player.FindItemByLayer(layer);
                h = (h * 17) ^ (item == null ? (int)layer : (int)item.Serial);
            }

            h ^= JournalManager.Entries.Count << 3;
            h ^= world.Macros.GetAllMacros().Count << 5;
            h ^= PadBindings.RadarRange << 7;

            return h;
        }

        // --- half-cut camera ---------------------------------------------------------------------

        private static void ApplyHalfCut()
        {
            Profile profile = ProfileManager.CurrentProfile;
            WorldViewportGump viewport = UIManager.GetGump<WorldViewportGump>();

            if (profile == null || viewport == null || Client.Game == null)
            {
                return;
            }

            if (!_cut)
            {
                _savedPos = profile.GameWindowPosition;
                _savedSize = profile.GameWindowSize;
                _savedFull = profile.GameWindowFullSize;
                _cut = true;
            }

            // Window pixels. ResizeGameWindow divides by DpiScale itself;
            // ClientBounds is already divided, and passing that made the
            // world a quarter of the window whenever the display scale was
            // not 1, leaving a gap of background on the left.
            //
            // The world is not clipped to the left half. Its centre (the
            // player) is the middle of the left half, and it runs out to the
            // right edge of the window, so it is drawn under the screen too:
            // the bag sits over the live world and its transparent texels
            // show the map. That makes it wider than the window, starting
            // off the left edge.
            Rectangle window = Client.Game.Window.ClientBounds;
            float dpi = Math.Max(0.01f, Client.Game.DpiScale);
            int centre = Math.Max(1, (int)MathF.Floor(window.Width * WorldShare * 0.5f));
            int worldW = Math.Max(1, 2 * (window.Width - centre));
            int worldH = Math.Max(1, window.Height);
            profile.GameWindowFullSize = false;
            Point sized = viewport.ResizeGameWindow(new Point(worldW, worldH));
            int left = (int)MathF.Floor(centre / dpi) - sized.X / 2;
            profile.GameWindowPosition = new Point(left, 0);
            profile.GameWindowSize = sized;
            viewport.SetGameWindowPosition(new Point(left - WorldViewportGump.BORDER_WIDTH, -WorldViewportGump.BORDER_WIDTH));
            // System chat stays in the visible left half, not off the edge.
            viewport.SetChatArea(-left, Math.Max(1, (int)MathF.Floor(window.Width * WorldShare / dpi)));
            // The viewport's tiled border would sit on the screen edges.
            // Hide it so a one-frame position correction cannot flash a strip there.
            viewport.SetFrameVisible(false);
            _cutX = left;
            _cutW = sized.X;
            _cutH = sized.Y;
        }

        private static bool CameraHeld()
        {
            var camera = Client.Game?.Scene?.Camera;

            return camera != null && camera.Bounds.X == _cutX && camera.Bounds.Y == 0
                && camera.Bounds.Width == _cutW && camera.Bounds.Height == _cutH
                && _cutW > 0 && _cutH > 0;
        }

        private static void RestoreCamera()
        {
            if (!_cut)
            {
                return;
            }

            Profile profile = ProfileManager.CurrentProfile;
            WorldViewportGump viewport = UIManager.GetGump<WorldViewportGump>();
            _cut = false;

            if (profile == null)
            {
                return;
            }

            profile.GameWindowFullSize = _savedFull;
            profile.GameWindowPosition = _savedPos;
            profile.GameWindowSize = _savedSize;

            if (viewport == null || Client.Game == null)
            {
                return;
            }

            viewport.SetFrameVisible(true);
            viewport.SetChatArea(0, 0);

            if (_savedFull)
            {
                viewport.ResizeGameWindow(new Point(Client.Game.ClientBounds.Width, Client.Game.ClientBounds.Height));
                viewport.SetGameWindowPosition(new Point(-WorldViewportGump.BORDER_WIDTH, -WorldViewportGump.BORDER_WIDTH));
            }
            else
            {
                viewport.SetGameWindowPosition(_savedPos);
                viewport.ResizeGameWindow(_savedSize);
            }
        }

        // --- the view ----------------------------------------------------------------------------

        private static void Show()
        {
            PadOverlay layer = PadOverlay.Get();

            if (layer == null || !PadArt.Warm())
            {
                return;
            }

            if (_root == null || !GodotObject.IsInstanceValid(_root))
            {
                Build(layer);
            }

            _root.Visible = true;
            Layout();
        }

        private static void Build(PadOverlay layer)
        {
            _root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore, Name = "PadScreen" };
            layer.Ui.AddChild(_root);

            // Button hints. Placed on the bag's right edge in PlaceHints.
            // Above the bag: a later sibling would draw over them.
            _hints = Overlay.Column(8);
            _hints.Name = "PadScreenHints";
            _hints.ZIndex = 8;
            _root.AddChild(_hints);

            _frame = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore };
            _frame.AddThemeStyleboxOverride("panel", Overlay.Frame(Overlay.Stone, 8));
            _frame.ClipContents = true;
            _root.AddChild(_frame);
            // Content scales inside the frame; art panels replace the stone with the gump.
            _content = new Control { MouseFilter = Control.MouseFilterEnum.Ignore, Name = "PadScreenContent" };
            _frame.AddChild(_content);
            VBoxContainer col = Overlay.Column(4);
            col.Name = "PadScreenCol";
            _content.AddChild(col);

            HBoxContainer header = Overlay.Row(6);
            _icon = PadArt.Pic(null);
            _icon.CustomMinimumSize = new Vector2(36, 28);
            _icon.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
            header.AddChild(_icon);
            _title = Overlay.Text("", UoTheme.Heading, 2); // scale refreshed in Paint
            _title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            header.AddChild(_title);
            _pageLabel = Overlay.Text("", UoTheme.Muted);
            header.AddChild(_pageLabel);
            col.AddChild(header);

            _stage = new Control { MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
            _stage.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            col.AddChild(_stage);

            // Upscaled pack / paperdoll art: the panel background, not a picture above a grid.
            _hero = PadArt.Pic(null);
            _hero.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
            _hero.StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
            _hero.TextureFilter = CanvasItem.TextureFilterEnum.Nearest;
            _stage.AddChild(_hero);

            // 9-slice of gump 0x003C. Fills the right half; the center is the pocket.
            // Transparent texels stay transparent: the world shows through them.
            _bag = new Panel
            {
                MouseFilter = Control.MouseFilterEnum.Ignore,
                Name = "PadPackSlice",
                Visible = false,
                TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
            };
            _stage.AddChild(_bag);

            _grid = new GridContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Columns = PackCols };
            _grid.AddThemeConstantOverride("h_separation", (int)CellGap);
            _grid.AddThemeConstantOverride("v_separation", (int)CellGap);
            _stage.AddChild(_grid);

            _scrollTrack = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore, Name = "PadScrollTrack" };
            _scrollTrack.AddThemeStyleboxOverride("panel", Overlay.Frame(Overlay.Parchment, 1));
            _stage.AddChild(_scrollTrack);
            _scrollThumb = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore, Name = "PadScrollThumb" };
            _scrollThumb.AddThemeStyleboxOverride("panel", Overlay.Frame(Overlay.Parchment, 1, 1));
            _scrollTrack.AddChild(_scrollThumb);

            _list = Overlay.Column(1);
            _list.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
            _stage.AddChild(_list);

            _detail = Overlay.Text("", UoTheme.Ink);
            _detail.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            col.AddChild(_detail);
        }

        private static int VisibleRows()
        {
            float h = _list?.Size.Y > 1f ? _list.Size.Y : (_root?.Size.Y ?? PadOverlay.ClientRect.Size.Y) * 0.5f;
            int fontPx = Math.Max(1, UoTheme.FontSize * ReadableFont(PadOverlay.UiScale));

            return Math.Max(3, (int)((h - 8f) / (fontPx + 8f)));
        }

        private static void Paint()
        {
            if (_stage == null || !GodotObject.IsInstanceValid(_stage) || Current is not WheelWindow w)
            {
                return;
            }

            bool grid = IsArtPanel;
            bool pack = Current == WheelWindow.Backpack;
            _hero.Visible = grid && !pack;
            if (_bag != null && GodotObject.IsInstanceValid(_bag))
            {
                _bag.Visible = pack;
            }
            _grid.Visible = grid;
            if (_scrollTrack != null && GodotObject.IsInstanceValid(_scrollTrack))
            {
                _scrollTrack.Visible = grid;
            }
            _list.Visible = !grid;

            // Art panels are the gump itself. No flat fill behind the bag.
            // Lists keep the stone frame, glued to the right half.
            if (_frame != null && GodotObject.IsInstanceValid(_frame))
            {
                _frame.AddThemeStyleboxOverride("panel", grid
                    ? new StyleBoxEmpty()
                    : Overlay.Frame(Overlay.Stone, 8));
            }

            // Header icon/title stay for list screens; art panels hide them (the gump is the label).
            Control header = _icon?.GetParent() as Control;
            if (header != null)
            {
                header.Visible = !grid;
            }

            _icon.Texture = PadArt.Art(PadArt.Icon(w));
            _title.Text = PadBindings.Name(w);
            int titleScale = Math.Max(1, (Current == WheelWindow.Journal ? ChatFontScale : MenuFontScale) + 1);
            _title.AddThemeFontSizeOverride("font_size", Math.Max(1, UoTheme.FontSize * titleScale));
            _pageLabel.Text = _pages > 1 ? $"{_page + 1}/{_pages}" : $"{_focus + 1}/{_lines.Count}";

            if (_focus >= 0 && _focus < _lines.Count)
            {
                Line cur = _lines[_focus];
                _detail.Text = string.IsNullOrEmpty(cur.Detail) ? (cur.Label ?? "") : $"{cur.Label}  ·  {cur.Detail}";
            }
            else
            {
                _detail.Text = "";
            }
            _detail.AddThemeFontSizeOverride("font_size", Math.Max(1, UoTheme.FontSize * FontScaleFor()));
            // Ink on bag leather / doll dark frame is invisible. Cream with a
            // dark outline matches the bag text on both art panels.
            bool onArt = pack || Current == WheelWindow.Paperdoll;
            _detail.AddThemeColorOverride("font_color", onArt ? UoTheme.Cream : UoTheme.Ink);
            _detail.AddThemeColorOverride("font_outline_color", onArt ? new Godot.Color(0f, 0f, 0f, 0.9f) : new Godot.Color(0f, 0f, 0f, 0f));
            _detail.AddThemeConstantOverride("outline_size", onArt ? 3 : 0);
            _detail.Visible = true;
            _detail.Modulate = Godot.Colors.White;
            _detail.ZIndex = 4;

            PaintHints();

            if (grid)
            {
                EnsureGridVisible();
                PaintGrid(w);
            }
            else
            {
                PaintList();
            }
        }

        private static void PaintGrid(WheelWindow w)
        {
            int stamp = GridStamp();

            if (stamp == _gridStamp && _grid.GetChildCount() > 0)
            {
                return;
            }

            _gridStamp = stamp;

            foreach (Node n in _grid.GetChildren())
            {
                _grid.RemoveChild(n);
                n.QueueFree();
            }

            _grid.Columns = _cols;
            Texture2D hero;
            if (w == WheelWindow.Backpack)
            {
                hero = PadArt.Gump(PackGump) ?? PadArt.Art(PadArt.Icon(w));
            }
            else
            {
                // Frame plus the player's body and worn gumps — empty 0x07d0 alone
                // is a black arch with no character.
                hero = ComposeDollHero(Client.Game?.UO?.World?.Player)
                    ?? PadArt.Gump(DollGump)
                    ?? PadArt.Art(PadArt.Icon(w));
            }
            _hero.Texture = hero;

            int cols = Math.Max(1, _cols);
            int rows = Math.Max(1, _rowsVisible);
            int first = _top * cols;
            int last = Math.Min(_lines.Count, first + rows * cols);
            float cell = Math.Max(8f, _cell);

            for (int i = first; i < last; i++)
            {
                Line line = _lines[i];
                bool lit = i == _focus;
                var cellPanel = new PanelContainer
                {
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                    CustomMinimumSize = new Vector2(cell, cell),
                    ClipContents = true,
                };
                // Unselected cells stay clear so the bag (or the doll) shows
                // through. The focused cell is an outline, not a filled tile.
                cellPanel.AddThemeStyleboxOverride("panel", lit ? FocusOutline() : new StyleBoxEmpty());

                // Bake a nearest-neighbour N× texture. StretchMode on TextureRect
                // did not enlarge DisplayedGraphic art (specks in 96px cells).
                Texture2D icon = line.Art == 0 ? null : ScaledIcon(line.Art, cell);
                float dw = icon?.GetWidth() ?? 1f;
                float dh = icon?.GetHeight() ?? 1f;
                var center = new CenterContainer
                {
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    SizeFlagsVertical = Control.SizeFlags.ExpandFill,
                };
                TextureRect art = PadArt.Pic(icon);
                art.CustomMinimumSize = new Vector2(dw, dh);
                art.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
                art.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
                art.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
                art.StretchMode = TextureRect.StretchModeEnum.Keep;
                art.TextureFilter = CanvasItem.TextureFilterEnum.Nearest;
                center.AddChild(art);
                cellPanel.AddChild(center);
                _grid.AddChild(cellPanel);
            }

            // Pad empty cells so the grid keeps its column count on a partial last row.
            int shown = last - first;
            int pad = (cols - (shown % cols)) % cols;

            for (int i = 0; i < pad; i++)
            {
                var empty = new Control
                {
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                    CustomMinimumSize = new Vector2(cell, cell),
                };
                _grid.AddChild(empty);
            }
        }

        private static void PaintList()
        {
            foreach (Node n in _list.GetChildren())
            {
                _list.RemoveChild(n);
                n.QueueFree();
            }

            int vis = VisibleRows();

            if (_focus < _top)
            {
                _top = _focus;
            }

            if (_focus >= _top + vis)
            {
                _top = _focus - vis + 1;
            }

            _top = Math.Max(0, _top);
            int end = Math.Min(_lines.Count, _top + vis);

            for (int i = _top; i < end; i++)
            {
                Line line = _lines[i];
                bool lit = i == _focus;
                int fontPx = Math.Max(1, UoTheme.FontSize * FontScaleFor());
                var row = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(0, fontPx + 6) };
                row.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                row.AddThemeStyleboxOverride("panel", lit
                    ? Overlay.Frame(Overlay.Parchment, 2, 1)
                    : new StyleBoxEmpty { ContentMarginLeft = 2, ContentMarginRight = 2, ContentMarginTop = 1, ContentMarginBottom = 1 });
                HBoxContainer box = Overlay.Row(4);
                TextureRect art = PadArt.Pic(line.Art == 0 ? null : PadArt.Art(line.Art));
                art.CustomMinimumSize = new Vector2(22, 14);
                art.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
                box.AddChild(art);
                Label label = Overlay.Text(line.Label ?? "", lit ? UoTheme.Danger : UoTheme.Ink, FontScaleFor(), true);
                label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                // Wrap inside the frame. A long skill name must not draw past the Deck edge.
                label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                label.ClipText = true;
                box.AddChild(label);

                if (!string.IsNullOrEmpty(line.Detail))
                {
                    box.AddChild(Overlay.Text(line.Detail, UoTheme.Muted));
                }

                row.AddChild(box);
                _list.AddChild(row);
            }
        }

        private static void PaintHints()
        {
            if (_hints == null || !GodotObject.IsInstanceValid(_hints))
            {
                return;
            }

            // Rebuilding this plate is a one-frame hole on the left. Position,
            // hits and mana change while the bag is open and must not do it.
            string key = HintKey();

            if (key == _hintKey && _hints.GetChildCount() > 0)
            {
                return;
            }

            _hintKey = key;

            foreach (Node n in _hints.GetChildren())
            {
                _hints.RemoveChild(n);
                n.QueueFree();
            }

            // Only as wide as the words. ExpandFill against a stretched parent
            // was what made this plate eat the world strip.
            var plate = new PanelContainer
            {
                MouseFilter = Control.MouseFilterEnum.Ignore,
                SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
                SizeFlagsVertical = Control.SizeFlags.ShrinkBegin,
            };
            plate.AddThemeStyleboxOverride("panel", Overlay.Frame(Overlay.Parchment, 4));
            VBoxContainer col = Overlay.Column(2);
            col.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
            plate.AddChild(col);
            _hints.AddChild(plate);

            void Hint(PadCommand command, string caption)
            {
                HBoxContainer pair = Overlay.Row(4);
                pair.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
                pair.AddChild(PadArt.Cap(PadBindings.For(command)));
                Label words = Overlay.Text(caption, UoTheme.Ink, FontScaleFor(), false);
                words.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
                words.AutowrapMode = TextServer.AutowrapMode.Off;
                words.ClipText = false;
                pair.AddChild(words);
                col.AddChild(pair);
            }

            Hint(PadCommand.Use, HasItemActions ? "Use" : "Select");
            Hint(PadCommand.Cancel, "Close");

            if (HasItemActions)
            {
                Hint(PadCommand.AttackLast, Current == WheelWindow.Paperdoll ? "Unequip" : "Drop");
                Hint(PadCommand.MacroRow, "Equip");
                Hint(PadCommand.Drawer, "More");
            }

            if (_pages > 1)
            {
                Hint(PadCommand.TargetLast, "Page");
                Hint(PadCommand.NextHostile, "Page");
            }
        }

        private static int FontScaleFor() => ReadableFont(PadOverlay.UiScale);

        /// <summary>
        /// Menu/journal font 1–3, bumped in art pixels when the debug UI scale
        /// is below 1 so the same letters stay about as tall on screen.
        /// Does not change <see cref="MenuFontScale"/> or <see cref="ChatFontScale"/>.
        /// </summary>
        private static int ReadableFont(float uiScale)
        {
            int scale = Current == WheelWindow.Journal ? ChatFontScale : MenuFontScale;

            if (uiScale < 1f && uiScale > 0.01f)
            {
                scale = Math.Clamp((int)MathF.Ceiling(scale / uiScale), scale, 6);
            }

            return Math.Max(1, scale);
        }

        /// <summary>Keep the focused grid cell inside the visible backpack rows.</summary>
        private static void EnsureGridVisible()
        {
            if (_cols <= 1 || _lines.Count == 0)
            {
                return;
            }

            int rows = Math.Max(1, _rowsVisible);
            int row = _focus / _cols;

            if (row < _top)
            {
                _top = row;
            }

            if (row >= _top + rows)
            {
                _top = row - rows + 1;
            }

            int maxTop = Math.Max(0, ((_lines.Count + _cols - 1) / _cols) - rows);
            _top = Math.Clamp(_top, 0, maxTop);
        }

        private static bool IsArtPanel =>
            Current is WheelWindow.Backpack or WheelWindow.Paperdoll;

        /// <summary>
        /// Content area of the open-pack gump (0x003C) as a fraction of the
        /// texture, matching classic container.def Left/Top/Right/Bottom.
        /// </summary>
        private static Rect2 PackContentFrac(Texture2D tex)
        {
            float w = Math.Max(1f, tex.GetWidth());
            float h = Math.Max(1f, tex.GetHeight());
            return new Rect2(
                PackLeft / w,
                PackTop / h,
                (PackRight - PackLeft) / w,
                (PackBottom - PackTop) / h);
        }

        private static void Layout()
        {
            if (_root == null || !GodotObject.IsInstanceValid(_root))
            {
                return;
            }

            Rect2 area = PadOverlay.ClientRect;
            _root.Position = area.Position.Round();
            _root.Size = area.Size;
            _bagInStage = default;

            // Right half: the menu. Frame is edge-anchored; UI scale never moves it.
            float left = MathF.Floor(area.Size.X * WorldShare);
            Vector2 framePos = new Vector2(left, 0f);
            Vector2 frameSize = new Vector2(area.Size.X - left, area.Size.Y);
            _frame.Position = framePos;
            _frame.Size = frameSize;
            _frameSize = frameSize;

            if (_hints != null && GodotObject.IsInstanceValid(_hints))
            {
                _hints.Visible = IsOpen;
            }

            bool art = IsArtPanel;

            if (_content != null && GodotObject.IsInstanceValid(_content))
            {
                float ps = art ? 1f : Math.Clamp(PanelScale, 1, 3);
                // The bag fills the half. A pad here would shrink it as UI scale changes.
                float pad = Current == WheelWindow.Backpack ? 0f : art ? 4f : 8f;
                _content.Scale = new Vector2(ps, ps);
                _content.Position = new Vector2(pad, pad);
                _content.Size = new Vector2((frameSize.X - pad * 2f) / ps, (frameSize.Y - pad * 2f) / ps);
                Control col = _content.GetNodeOrNull<Control>("PadScreenCol");
                if (col != null)
                {
                    col.Size = _content.Size;
                }
            }

            if (art && (Current == WheelWindow.Backpack || (_hero.Visible && _hero.Texture != null)))
            {
                // Pull the detail out of the column first so the VBox does not
                // reserve a strip and then clip it.
                if (_detail != null && GodotObject.IsInstanceValid(_detail) && _stage != null && _detail.GetParent() != _stage)
                {
                    _detail.GetParent()?.RemoveChild(_detail);
                    _stage.AddChild(_detail);
                    _detail.CustomMinimumSize = Vector2.Zero;
                }

                _stage.Position = Vector2.Zero;
                _stage.Size = _content.Size;

                if (Current == WheelWindow.Backpack)
                {
                    LayoutPack();
                }
                else
                {
                    LayoutArtPanel();
                }
            }
            else if (_list.Visible)
            {
                _bagInStage = default;
                _stage.Position = Vector2.Zero;
                _stage.Size = _content != null ? _content.Size : _stage.Size;
                ParkDetailInColumn();

                if (_scrollTrack != null && GodotObject.IsInstanceValid(_scrollTrack))
                {
                    _scrollTrack.Visible = false;
                }

                _list.Position = Vector2.Zero;
                _list.Size = _stage.Size;
            }

            PlaceHints(area);
        }

        /// <summary>
        /// How a pack or paperdoll grid fits a panel. Cell size shrinks (down
        /// to <see cref="CellMin"/>) so the columns sit inside the gump's
        /// content pocket instead of spilling out of it. <paramref name="uiScale"/>
        /// only affects how tall the detail strip is (font boost below 1).
        /// </summary>
        private readonly struct ArtFit
        {
            public float HeroScale { get; init; }
            public float DetailRoom { get; init; }
            public float Cell { get; init; }
            public int Cols { get; init; }
            public int Rows { get; init; }
            public Rect2 Frac { get; init; }
        }

        private const float CellMin = 48f;
        private const float CellMax = 96f;
        // Classic 0x003C in this install (gumpartLegacyMUL). Preview uses these;
        // the live panel uses the cropped texture's real size (straps removed).
        private const float PackTexW = 230f, PackTexH = 204f;
        // Live metrics after CropPackStraps. Start as classic; EnsurePackSlice updates.
        private static float _packW = PackTexW, _packH = PackTexH;
        private static float _packL = PackLeft, _packT = PackTop, _packR = PackRight, _packB = PackBottom;
        private static Texture2D _packBody;
        private static ulong _packBodyOf;
        private static Texture2D _dollHero;
        private static int _dollStamp = int.MinValue;
        private static readonly System.Collections.Generic.Dictionary<(ushort, int), Texture2D> _iconScaleCache = new();



        private static ArtFit MeasureArt(float stageW, float stageH, Vector2 texSize, Rect2 frac, int wantCols, float uiScale, bool scrollBar)
        {
            int fontPx = Math.Max(1, UoTheme.FontSize * ReadableFont(uiScale));
            float detail = Math.Clamp(fontPx * 2f + 12f, 36f, Math.Min(120f, Math.Max(36f, stageH * 0.28f)));
            float maxW = Math.Max(8f, stageW);
            float maxH = Math.Max(48f, stageH - detail - 4f);
            float heroScale = Math.Min(maxW / Math.Max(1f, texSize.X), maxH / Math.Max(1f, texSize.Y));

            if (heroScale >= 2f)
            {
                heroScale = MathF.Floor(heroScale);
            }
            else if (heroScale < 1f)
            {
                heroScale = Math.Max(heroScale, 0.5f);
            }

            float pocketW = Math.Max(8f, frac.Size.X * texSize.X * heroScale - (scrollBar ? 14f : 0f));
            float pocketH = Math.Max(8f, frac.Size.Y * texSize.Y * heroScale);
            int cols = Math.Max(1, wantCols);

            while (cols > 2 && (pocketW - (cols - 1) * CellGap) / cols < CellMin)
            {
                cols--;
            }

            float cell = (pocketW - (cols - 1) * CellGap) / cols;
            cell = Math.Clamp(cell, Math.Min(CellMin, pocketW), CellMax);
            int rows = Math.Max(1, (int)MathF.Floor((pocketH + CellGap) / (cell + CellGap)));

            while (rows > 1 && rows * cell + (rows - 1) * CellGap > pocketH + 0.5f)
            {
                rows--;
            }

            return new ArtFit
            {
                HeroScale = heroScale,
                DetailRoom = detail,
                Cell = cell,
                Cols = cols,
                Rows = rows,
                Frac = frac,
            };
        }

        /// <summary>
        /// Backpack cells in a <paramref name="windowW"/>×<paramref name="windowH"/>
        /// client at <paramref name="uiScale"/>, using the classic 230×204 pack
        /// gump and reserving the scrollbar. The bag's screen size is the right
        /// half at every scale; this is only how many squares fit inside it.
        /// </summary>
        public static (int Columns, int Rows, int Cells, float CellSize) PreviewBackpack(float windowW, float windowH, float uiScale)
        {
            float scale = Math.Max(0.25f, uiScale);
            float stageW = windowW / scale * (1f - WorldShare);
            float stageH = windowH / scale;
            PackFit fit = MeasurePack(stageW, stageH, scale, true);

            return (fit.Cols, fit.Rows, fit.Cols * fit.Rows, fit.Cell);
        }

        private readonly struct PackFit
        {
            public float Detail { get; init; }
            public float Cell { get; init; }
            public int Cols { get; init; }
            public int Rows { get; init; }
            public float MarginL { get; init; }
            public float MarginT { get; init; }
            public float MarginR { get; init; }
            public float MarginB { get; init; }
            public Vector2 Origin { get; init; }
            public Vector2 Bag { get; init; }
            /// <summary>Window pixels per texture pixel. Independent of UI scale.</summary>
            public float Draw { get; init; }
            /// <summary>Stylebox rect before <see cref="Draw"/> is applied.</summary>
            public Vector2 Local { get; init; }
        }

        /// <summary>
        /// The open-pack gump filling the stage (the right half). Classic
        /// margins stay a fixed fraction of that rectangle, so the bag's
        /// screen size and its frame do not change with <paramref name="uiScale"/>.
        /// The scale only changes how many <see cref="Cell"/>-sized squares
        /// fit in the pocket. <paramref name="scroll"/> reserves the scrollbar.
        /// </summary>
        private static PackFit MeasurePack(float stageW, float stageH, float uiScale, bool scroll)
        {
            float scale = Math.Max(0.25f, uiScale);
            float bagW = Math.Max(8f, stageW);
            float bagH = Math.Max(8f, stageH);
            float screenW = bagW * scale;
            float screenH = bagH * scale;
            // Uniform corners. The tight axis matches the (strap-cropped) gump;
            // the other stretches through the 9-slice center so the bag fills
            // the half as a square-ish satchel.
            float texW = Math.Max(8f, _packW);
            float texH = Math.Max(8f, _packH);
            float draw = Math.Min(screenW / texW, screenH / texH);
            draw = Math.Max(draw, 0.05f);
            float px = draw / scale;
            float ml = _packL * px;
            float mt = _packT * px;
            float mr = (texW - _packR) * px;
            float mb = (texH - _packB) * px;
            float scrollArt = scroll ? ScrollScreen / scale : 0f;
            float pocketW = Math.Max(8f, bagW - ml - mr - scrollArt);
            float pocketH = Math.Max(8f, bagH - mt - mb);
            // Prefer larger icons: start from the preferred Cell, never more
            // columns than PackCols, and grow the cell up to CellMax.
            int cols = Math.Max(1, (int)MathF.Floor((pocketW + CellGap) / (Cell + CellGap)));
            cols = Math.Min(cols, PackCols);
            float cell = (pocketW - (cols - 1) * CellGap) / Math.Max(1, cols);

            while (cols > 1 && cell < CellMin)
            {
                cols--;
                cell = (pocketW - (cols - 1) * CellGap) / cols;
            }

            // If the pocket still has room, drop a column so icons grow (whole cells).
            while (cols > 2 && cell < CellMax * 0.85f)
            {
                int fewer = cols - 1;
                float bigger = (pocketW - (fewer - 1) * CellGap) / fewer;
                if (bigger > CellMax + 0.5f)
                {
                    break;
                }
                cols = fewer;
                cell = bigger;
            }

            cell = Math.Min(cell, CellMax);

            if (cell > pocketH)
            {
                cell = Math.Max(8f, pocketH);
            }

            int rows = Math.Max(1, (int)MathF.Floor((pocketH + CellGap) / (cell + CellGap)));

            while (rows > 1 && rows * cell + (rows - 1) * CellGap > pocketH + 0.5f)
            {
                rows--;
            }

            return new PackFit
            {
                Detail = mb,
                Cell = cell,
                Cols = cols,
                Rows = rows,
                MarginL = ml,
                MarginT = mt,
                MarginR = mr,
                MarginB = mb,
                Origin = Vector2.Zero,
                Bag = new Vector2(bagW, bagH),
                Draw = draw,
                Local = new Vector2(screenW / draw, screenH / draw),
            };
        }

        /// <summary>
        /// Fade the classic pack's centre strap in the pocket so item icons
        /// stay clear of it. Samples neighbouring leather to fill.
        /// </summary>
        private static void ClearPackStrap(Image body, float sx, float sy, int cropL)
        {
            if (body == null)
            {
                return;
            }

            int bw = body.GetWidth();
            int bh = body.GetHeight();
            // Strap sits near the horizontal middle of the classic pocket.
            float pocketMidX = ((PackLeft + PackRight) * 0.5f) * sx - cropL;
            int strapX = (int)MathF.Round(pocketMidX);
            int strapW = Math.Max(6, (int)MathF.Round(10f * sx));
            int top = Math.Max(0, (int)MathF.Round(PackTop * sy));
            int bottom = Math.Min(bh, (int)MathF.Round(PackBottom * sy));
            int x0 = Math.Clamp(strapX - strapW / 2, 0, bw - 1);
            int x1 = Math.Clamp(strapX + strapW / 2, 0, bw - 1);
            int sampleL = Math.Max(0, x0 - strapW);
            int sampleR = Math.Min(bw - 1, x1 + strapW);

            for (int y = top; y < bottom; y++)
            {
                Godot.Color left = body.GetPixel(sampleL, y);
                Godot.Color right = body.GetPixel(sampleR, y);
                Godot.Color fill = left.A >= right.A ? left : right;

                if (fill.A < 0.05f)
                {
                    continue;
                }

                for (int x = x0; x <= x1; x++)
                {
                    // Soft edges: keep a little of the original at the sides.
                    float edge = Math.Min(x - x0, x1 - x) / (float)Math.Max(1, strapW / 2);
                    float t = Math.Clamp(edge * 1.5f, 0.35f, 1f);
                    Godot.Color c = body.GetPixel(x, y);
                    body.SetPixel(x, y, c.Lerp(fill, t));
                }
            }
        }

        private static void EnsurePackSlice(Texture2D tex)
        {
            if (tex == null || _bag == null)
            {
                return;
            }

            Texture2D body = PackBody(tex);
            int tw = Math.Max(1, body.GetWidth());
            int th = Math.Max(1, body.GetHeight());
            float sx = tw / Math.Max(1f, _packW);
            float sy = th / Math.Max(1f, _packH);
            int left = Math.Max(1, (int)MathF.Round(_packL * sx));
            int top = Math.Max(1, (int)MathF.Round(_packT * sy));
            int right = Math.Max(1, (int)MathF.Round((_packW - _packR) * sx));
            int bottom = Math.Max(1, (int)MathF.Round((_packH - _packB) * sy));

            if (left + right >= tw)
            {
                left = right = Math.Max(1, tw / 6);
            }

            if (top + bottom >= th)
            {
                top = bottom = Math.Max(1, th / 6);
            }

            int key = HashCode.Combine(tw, th, left, top, right, bottom, unchecked((int)body.GetInstanceId()));

            if (key == _sliceKey)
            {
                return;
            }

            _sliceKey = key;
            _bag.AddThemeStyleboxOverride("panel", new StyleBoxTexture
            {
                Texture = body,
                TextureMarginLeft = left,
                TextureMarginTop = top,
                TextureMarginRight = right,
                TextureMarginBottom = bottom,
                AxisStretchHorizontal = StyleBoxTexture.AxisStretchMode.Stretch,
                AxisStretchVertical = StyleBoxTexture.AxisStretchMode.Stretch,
            });
        }

        /// <summary>
        /// Classic open-pack gump with the left/right straps cropped away so
        /// only the square-ish bag body and open flap remain. Pocket margins
        /// are rewritten for the cropped texture. Faithful UO art — no new skin.
        /// </summary>
        private static Texture2D PackBody(Texture2D tex)
        {
            ulong id = tex.GetInstanceId();

            if (_packBody != null && GodotObject.IsInstanceValid(_packBody) && _packBodyOf == id)
            {
                return _packBody;
            }

            Image src = tex.GetImage();

            if (src == null)
            {
                return tex;
            }

            if (src.GetFormat() != Image.Format.Rgba8)
            {
                src.Convert(Image.Format.Rgba8);
            }

            int w = src.GetWidth();
            int h = src.GetHeight();
            float sx = w / PackTexW;
            float sy = h / PackTexH;
            // Never crop into the classic pocket. Straps live in the side margins.
            // Leave a 2px guard into the pocket so a right-side strap stub is gone.
            int maxCropL = Math.Max(0, (int)MathF.Floor(PackLeft * sx) - 2);
            int maxCropR = Math.Max(0, (int)MathF.Floor((PackTexW - PackRight) * sx) - 2);
            var counts = new int[w];

            for (int x = 0; x < w; x++)
            {
                int n = 0;

                for (int y = 0; y < h; y++)
                {
                    if (src.GetPixel(x, y).A > 0.12f)
                    {
                        n++;
                    }
                }

                counts[x] = n;
            }

            // Straps are thin side loops; the body fills most of a column.
            int thresh = Math.Max(12, (int)(h * 0.32f));
            int left = 0;
            int right = w - 1;

            while (left < w && counts[left] < thresh)
            {
                left++;
            }

            while (right > left && counts[right] < thresh)
            {
                right--;
            }

            int cropL = Math.Clamp(left, 0, maxCropL);
            int cropR = Math.Clamp(w - 1 - right, 0, maxCropR);

            // If column density did not find straps (already cropped, or odd
            // art), fall back to a classic ~24px side trim inside the margin.
            if (cropL < 10 && cropR < 10 && maxCropL >= 16 && maxCropR >= 16)
            {
                cropL = Math.Min(28, maxCropL);
                cropR = Math.Min(28, maxCropR);
            }

            // Prefer a slightly heavier right trim — a strap stub often remains there.
            if (cropR < cropL && maxCropR > cropR)
            {
                cropR = Math.Min(maxCropR, cropR + 4);
            }

            if (cropL + cropR >= w - 8)
            {
                cropL = cropR = 0;
            }

            Image body = cropL == 0 && cropR == 0
                ? (Image)src.Duplicate()
                : src.GetRegion(new Rect2I(cropL, 0, w - cropL - cropR, h));
            // Soft-clear the centre strap / buckle column inside the pocket so
            // grid cells (often 6 and 7) do not sit under leather strap art.
            ClearPackStrap(body, sx, sy, cropL);
            _packW = body.GetWidth();
            _packH = body.GetHeight();
            _packL = Math.Max(4f, PackLeft * sx - cropL);
            _packT = PackTop * sy;
            _packR = Math.Min(_packW - 4f, PackRight * sx - cropL);
            _packB = PackBottom * sy;
            _packBody = ImageTexture.CreateFromImage(body);
            _packBodyOf = id;
            return _packBody;
        }

        private static void LayoutPack()
        {
            Texture2D tex = PadArt.Gump(PackGump);
            float stageW = Math.Max(8f, _stage.Size.X);
            float stageH = Math.Max(8f, _stage.Size.Y);
            PackFit fit = MeasurePack(stageW, stageH, PadOverlay.UiScale, false);
            int colsForScroll = Math.Max(1, fit.Cols);
            int totalForCols = Math.Max(1, (_lines.Count + colsForScroll - 1) / colsForScroll);
            bool scrolled = totalForCols > fit.Rows;

            if (scrolled)
            {
                fit = MeasurePack(stageW, stageH, PadOverlay.UiScale, true);
            }

            EnsurePackSlice(tex);
            _hero.Visible = false;
            _bagInStage = new Rect2(fit.Origin, fit.Bag);

            if (_bag != null)
            {
                // Local rect is the gump, scaled up to the half. Cancelling
                // UiScale here keeps that screen size fixed while the grid,
                // in art pixels, gains or loses squares.
                float scale = Math.Max(0.25f, PadOverlay.UiScale);
                _bag.Visible = true;
                _bag.PivotOffset = Vector2.Zero;
                _bag.Scale = new Vector2(fit.Draw / scale, fit.Draw / scale);
                _bag.Position = fit.Origin;
                _bag.Size = fit.Local;
            }

            float scrollW = scrolled ? ScrollScreen / Math.Max(0.25f, PadOverlay.UiScale) : 0f;
            Rect2 content = new Rect2(
                fit.Origin.X + fit.MarginL,
                fit.Origin.Y + fit.MarginT,
                Math.Max(8f, fit.Bag.X - fit.MarginL - fit.MarginR - scrollW),
                Math.Max(8f, fit.Bag.Y - fit.MarginT - fit.MarginB));

            int prevRows = _rowsVisible;
            int prevCols = _cols;
            int prevTop = _top;
            float prevCell = _cell;
            _cols = fit.Cols;
            _cell = fit.Cell;
            _rowsVisible = fit.Rows;
            EnsureGridVisible();

            if (prevRows != _rowsVisible || prevCols != _cols || prevTop != _top
                || Math.Abs(prevCell - _cell) > 0.5f || _grid.GetChildCount() == 0)
            {
                _gridStamp = int.MinValue;

                if (Current is WheelWindow w)
                {
                    PaintGrid(w);
                }
            }

            int cols = Math.Max(1, _cols);
            float gridW = cols * _cell + (cols - 1) * CellGap;
            float gridH = _rowsVisible * _cell + (_rowsVisible - 1) * CellGap;
            // Leave a pocket strip under the grid for the focused-item name.
            float detailReserve = Math.Clamp(content.Size.Y * 0.18f, 28f, 56f);
            float gridRoomH = Math.Max(gridH, content.Size.Y - detailReserve);
            float gridX = content.Position.X + Math.Max(0f, (content.Size.X - gridW) * 0.5f);
            float gridY = content.Position.Y + Math.Max(0f, (Math.Min(gridRoomH, content.Size.Y - detailReserve) - gridH) * 0.35f);
            _grid.Position = new Vector2(gridX, gridY);
            _grid.Size = new Vector2(gridW, gridH);

            int totalRows = Math.Max(1, (_lines.Count + cols - 1) / cols);
            bool needScroll = totalRows > _rowsVisible;

            if (_scrollTrack != null && GodotObject.IsInstanceValid(_scrollTrack))
            {
                _scrollTrack.Visible = needScroll;

                if (needScroll)
                {
                    float barW = Math.Max(6f, ScrollScreen / Math.Max(0.25f, PadOverlay.UiScale) - 4f);
                    _scrollTrack.Position = new Vector2(content.End.X + 2f, content.Position.Y);
                    _scrollTrack.Size = new Vector2(barW, content.Size.Y);
                    float thumbH = Math.Max(16f, content.Size.Y * (_rowsVisible / (float)totalRows));
                    float travel = Math.Max(0f, content.Size.Y - thumbH);
                    float t = totalRows <= _rowsVisible ? 0f : _top / (float)(totalRows - _rowsVisible);
                    _scrollThumb.Position = new Vector2(1f, t * travel);
                    _scrollThumb.Size = new Vector2(barW - 2f, thumbH);
                }
            }

            if (_detail != null && GodotObject.IsInstanceValid(_detail) && _stage != null)
            {
                if (_detail.GetParent() != _stage)
                {
                    _detail.GetParent()?.RemoveChild(_detail);
                    _stage.AddChild(_detail);
                }

                _detail.Visible = true;
                // Empty pocket under the grid — above the clasp (lower ~40% of MarginB).
                float pocketBottom = content.Position.Y + content.Size.Y;
                float claspGuard = Math.Max(12f, fit.MarginB * 0.45f);
                float detailBottom = Math.Min(stageH - 2f, fit.Origin.Y + fit.Bag.Y - claspGuard);
                float detailTop = Math.Min(detailBottom - 24f, _grid.Position.Y + _grid.Size.Y + 8f);
                detailTop = Math.Max(content.Position.Y + 4f, detailTop);
                float detailH = Math.Max(24f, detailBottom - detailTop);

                // Larger cream text so the name reads on leather.
                int detailFont = Math.Max(2, FontScaleFor() + 1);
                _detail.AddThemeFontSizeOverride("font_size", Math.Max(1, UoTheme.FontSize * detailFont));
                _detail.CustomMinimumSize = Vector2.Zero;
                _detail.Scale = Vector2.One;
                _detail.Position = new Vector2(content.Position.X + 4f, detailTop);
                _detail.Size = new Vector2(Math.Max(8f, content.Size.X - 8f), detailH);
                _detail.HorizontalAlignment = HorizontalAlignment.Center;
                _detail.VerticalAlignment = VerticalAlignment.Top;
                _detail.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                _detail.ClipText = false;
                _detail.ZIndex = 4;
                // Under the grid, inside the bag body — raise above the 9-slice.
                if (_detail.GetIndex() < _stage.GetChildCount() - 1)
                {
                    _stage.MoveChild(_detail, _stage.GetChildCount() - 1);
                }
            }
        }

        /// <summary>
        /// Right-align the A/B/X/Y plate to the bag's left edge on the left
        /// half of the screen — against the satchel, not the bottom-right
        /// corner and not the far left of the world strip.
        /// </summary>
        private static void PlaceHints(Rect2 area)
        {
            if (_hints == null || !GodotObject.IsInstanceValid(_hints) || !IsOpen)
            {
                return;
            }

            foreach (Node n in _hints.FindChildren("*", "Label", true, false))
            {
                if (n is Label lab)
                {
                    lab.AutowrapMode = TextServer.AutowrapMode.Off;
                    lab.ClipText = false;
                    lab.CustomMinimumSize = Vector2.Zero;
                    lab.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
                }
            }

            Vector2 hintSize = _hints.GetCombinedMinimumSize();

            if (hintSize.X < 8f || hintSize.Y < 8f)
            {
                return;
            }

            // Hints live on the left half. Cap width to that strip so the
            // plate does not spill under the bag.
            float half = area.Size.X * WorldShare;
            float maxW = Math.Max(48f, half * 0.55f);
            float maxH = Math.Max(28f, area.Size.Y * 0.5f);
            float k = 1f;

            if (hintSize.X > maxW)
            {
                k = maxW / hintSize.X;
            }

            if (hintSize.Y * k > maxH)
            {
                k = Math.Min(k, maxH / hintSize.Y);
            }

            k = Math.Clamp(k, 0.4f, 1f);
            _hints.Scale = new Vector2(k, k);
            _hints.Size = hintSize;
            _hints.ClipContents = false;
            Vector2 drawn = hintSize * k;
            float contentX = _frame.Position.X + (_content?.Position.X ?? 0f);
            float contentY = _frame.Position.Y + (_content?.Position.Y ?? 0f);
            float bagLeft;
            float bagBottom;

            if (_bagInStage.Size.X > 8f && Current == WheelWindow.Backpack)
            {
                bagLeft = contentX + _bagInStage.Position.X;
                bagBottom = contentY + _bagInStage.End.Y;
            }
            else
            {
                bagLeft = _frame.Position.X;
                bagBottom = _frame.Position.Y + _frame.Size.Y - 6f;
            }

            // Right edge of the plate kisses the bag's left edge.
            float x = bagLeft - drawn.X - 6f;
            float y = bagBottom - drawn.Y - 6f;
            x = Math.Clamp(x, 2f, Math.Max(2f, bagLeft - drawn.X - 2f));
            y = Math.Clamp(y, 2f, Math.Max(2f, area.Size.Y - drawn.Y - 2f));
            _hints.Position = new Vector2(MathF.Round(x), MathF.Round(y));
        }

        private static float BackdropBottom()
        {
            if (_bag != null && GodotObject.IsInstanceValid(_bag) && _bag.Visible)
            {
                return _bag.Position.Y + _bag.Size.Y * _bag.Scale.Y;
            }

            if (_hero != null && GodotObject.IsInstanceValid(_hero))
            {
                return _hero.Position.Y + _hero.Size.Y;
            }

            return 0f;
        }

        private static StyleBox FocusOutline() => new StyleBoxFlat
        {
            BgColor = new Godot.Color(0f, 0f, 0f, 0f),
            BorderColor = new Godot.Color(0.95f, 0.78f, 0.28f, 1f),
            BorderWidthLeft = 2,
            BorderWidthTop = 2,
            BorderWidthRight = 2,
            BorderWidthBottom = 2,
            ContentMarginLeft = 2,
            ContentMarginTop = 2,
            ContentMarginRight = 2,
            ContentMarginBottom = 2,
        };

        private static string HintKey()
        {
            var key = new System.Text.StringBuilder();
            key.Append(FontScaleFor()).Append('|').Append(Current).Append('|').Append(_pages).Append('|').Append(HasItemActions);
            return key.ToString();
        }

        private static int GridStamp()
        {
            int cols = Math.Max(1, _cols);
            int h = _focus * 17 ^ _top * 31 ^ cols * 13 ^ _rowsVisible ^ _lines.Count ^ (int)(_cell * 10f);
            int first = _top * cols;
            int last = Math.Min(_lines.Count, first + Math.Max(1, _rowsVisible) * cols);

            for (int i = first; i < last; i++)
            {
                h = (h * 31) ^ (int)_lines[i].Serial ^ _lines[i].Art ^ (i == _focus ? 1 : 0);
            }

            return h;
        }

        /// <summary>
        /// Largest whole-number nearest upscale of item art that fits in ~70% of
        /// <paramref name="cell"/>. Baked into a new texture so draw size is real.
        /// </summary>
        private static Texture2D ScaledIcon(ushort graphic, float cell)
        {
            Texture2D src = PadArt.Art(graphic);

            if (src == null)
            {
                return null;
            }

            float iw = src.GetWidth();
            float ih = src.GetHeight();

            if (iw < 1f || ih < 1f)
            {
                return src;
            }

            float room = Math.Max(8f, cell * 0.70f);
            int scale = Math.Max(1, (int)MathF.Floor(Math.Min(room / iw, room / ih)));
            scale = Math.Min(scale, 12);

            if (scale <= 1)
            {
                return src;
            }

            var key = (graphic, scale);

            if (_iconScaleCache.TryGetValue(key, out Texture2D cached) && GodotObject.IsInstanceValid(cached))
            {
                return cached;
            }

            Image img = src.GetImage();

            if (img == null)
            {
                return src;
            }

            img = (Image)img.Duplicate();
            img.Resize(Math.Max(1, (int)(iw * scale)), Math.Max(1, (int)(ih * scale)), Image.Interpolation.Nearest);
            Texture2D baked = ImageTexture.CreateFromImage(img);
            _iconScaleCache[key] = baked;
            return baked;
        }

        /// <summary>
        /// Classic paperdoll frame with the mobile's body and equipment gumps
        /// blitted on top (same layer order as <see cref="PaperDollInteractable"/>).
        /// </summary>
        private static Texture2D ComposeDollHero(PlayerMobile mobile)
        {
            if (mobile == null || mobile.IsDestroyed)
            {
                return null;
            }

            int stamp = DollEquipStamp(mobile);

            if (_dollHero != null && stamp == _dollStamp)
            {
                return _dollHero;
            }

            Texture2D frameTex = PadArt.Gump(DollGump);

            if (frameTex == null)
            {
                return null;
            }

            Image frame = frameTex.GetImage();

            if (frame == null)
            {
                return null;
            }

            Image canvas = (Image)frame.Duplicate();
            ushort body = DollBodyGump(mobile);
            BlitGump(canvas, body, mobile.Hue);
            Span<ushort> layerGraphics = stackalloc ushort[PaperdollOrder.N];
            PaperdollOrder.GraphicsFromEntity(mobile, layerGraphics);
            bool altTorso = mobile.IsFemale || DollIsGargoyle(mobile.Graphic);
            Span<Layer> order = stackalloc Layer[PaperdollOrder.N];
            PaperdollOrder.Build(layerGraphics, altTorso, order);
            Span<Layer> layers = stackalloc Layer[PaperdollOrder.N];
            int layerCount = PaperdollOrder.Filter(order, includeBackpack: false, layers);

            for (int i = 0; i < layerCount; i++)
            {
                Layer layer = layers[i];
                Item equip = mobile.FindItemByLayer(layer);

                if (equip == null || Mobile.IsCovered(mobile, layer))
                {
                    continue;
                }

                ushort id = DollEquipGump(mobile, equip);
                BlitGump(canvas, id, (ushort)(equip.Hue & 0x3FFF));
            }

            _dollHero = ImageTexture.CreateFromImage(canvas);
            _dollStamp = stamp;
            return _dollHero;
        }

        private static int DollEquipStamp(PlayerMobile mobile)
        {
            int h = mobile.Graphic ^ (mobile.Hue << 5) ^ (mobile.IsFemale ? 1 : 0);

            foreach (Layer layer in Worn)
            {
                Item item = mobile.FindItemByLayer(layer);
                h = (h * 31) ^ (item == null ? 0 : (int)item.Serial ^ item.DisplayedGraphic ^ item.Hue);
            }

            Item hair = mobile.FindItemByLayer(Layer.Hair);
            Item beard = mobile.FindItemByLayer(Layer.Beard);
            h = (h * 31) ^ (int)(hair?.Serial ?? 0) ^ (int)(beard?.Serial ?? 0);
            return h;
        }

        private static ushort DollBodyGump(Mobile mobile)
        {
            if (mobile.Graphic == 0x0191 || mobile.Graphic == 0x0193) return 0x000D;
            if (mobile.Graphic == 0x025D) return 0x000E;
            if (mobile.Graphic == 0x025E) return 0x000F;
            if (mobile.Graphic == 0x029A || mobile.Graphic == 0x02B6) return 0x029A;
            if (mobile.Graphic == 0x029B || mobile.Graphic == 0x02B7) return 0x0299;
            if (mobile.Graphic == 0x04E5) return 0xC835;
            if (mobile.Graphic == 0x03DB) return 0x000C;
            return mobile.IsFemale ? (ushort)0x000D : (ushort)0x000C;
        }

        private static bool DollIsGargoyle(ushort graphic) =>
            graphic is 0x029A or 0x029B or 0x02B6 or 0x02B7;

        private static ushort DollEquipGump(Mobile mobile, Item equip)
        {
            ushort animID = equip.ItemData.AnimID;
            int offset = mobile.IsFemale ? Constants.FEMALE_GUMP_OFFSET : Constants.MALE_GUMP_OFFSET;
            ushort mobileGraphic = mobile.Graphic;
            Client.Game.UO.Animations.ConvertBodyIfNeeded(ref mobileGraphic);

            if (Client.Game.UO.FileManager.Animations.EquipConversions.TryGetValue(
                    mobileGraphic, out System.Collections.Generic.Dictionary<ushort, EquipConvData> dict)
                && dict.TryGetValue(animID, out EquipConvData data))
            {
                if (data.Gump > Constants.MALE_GUMP_OFFSET)
                {
                    animID = (ushort)(data.Gump >= Constants.FEMALE_GUMP_OFFSET
                        ? data.Gump - Constants.FEMALE_GUMP_OFFSET
                        : data.Gump - Constants.MALE_GUMP_OFFSET);
                }
                else
                {
                    animID = data.Gump;
                }
            }

            ushort id = (ushort)(animID + offset);

            if (Client.Game.UO.Gumps.GetGump(id).Texture == null && mobile.IsFemale)
            {
                id = (ushort)(animID + Constants.MALE_GUMP_OFFSET);
            }

            return id;
        }

        private static void BlitGump(Image canvas, ushort gumpId, ushort hue)
        {
            _ = hue; // hue shader lives on GumpPic; unhued blit still shows the character
            Texture2D tex = PadArt.Gump(gumpId);

            if (tex == null)
            {
                return;
            }

            Image src = tex.GetImage();

            if (src == null)
            {
                return;
            }

            int w = Math.Min(canvas.GetWidth(), src.GetWidth());
            int h = Math.Min(canvas.GetHeight(), src.GetHeight());

            if (w <= 0 || h <= 0)
            {
                return;
            }

            // Alpha-blend equipment over the frame/body.
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    Godot.Color over = src.GetPixel(x, y);

                    if (over.A < 0.02f)
                    {
                        continue;
                    }

                    Godot.Color under = canvas.GetPixel(x, y);
                    float a = over.A;
                    canvas.SetPixel(x, y, new Godot.Color(
                        over.R * a + under.R * (1f - a),
                        over.G * a + under.G * (1f - a),
                        over.B * a + under.B * (1f - a),
                        Math.Min(1f, under.A + a)));
                }
            }
        }

        /// <summary>
        /// Blow the pack / paperdoll gump up as the panel background; lay the
        /// item grid inside its classic content area; details sit below.
        /// </summary>
        private static void LayoutArtPanel()
        {
            Texture2D tex = _hero.Texture;
            Vector2 texSize = tex.GetSize();
            float stageW = Math.Max(8f, _stage.Size.X);
            float stageH = Math.Max(8f, _stage.Size.Y);
            Rect2 frac = Current == WheelWindow.Backpack
                ? PackContentFrac(tex)
                : new Rect2(0.08f, 0.10f, 0.84f, 0.72f);
            int wantCols = Current == WheelWindow.Paperdoll ? DollCols : PackCols;
            ArtFit fit = MeasureArt(stageW, stageH, texSize, frac, wantCols, PadOverlay.UiScale, false);
            int totalForCols = Math.Max(1, (_lines.Count + Math.Max(1, fit.Cols) - 1) / Math.Max(1, fit.Cols));
            bool scrolled = totalForCols > fit.Rows;

            if (scrolled)
            {
                fit = MeasureArt(stageW, stageH, texSize, frac, wantCols, PadOverlay.UiScale, true);
            }

            float s = fit.HeroScale;
            Vector2 heroSize = texSize * s;
            float maxH = Math.Max(48f, stageH - fit.DetailRoom - 4f);
            _hero.CustomMinimumSize = Vector2.Zero;
            _hero.Size = heroSize;
            _hero.Position = new Vector2((stageW - heroSize.X) * 0.5f, Math.Max(0f, (maxH - heroSize.Y) * 0.35f));

            Rect2 content = new Rect2(
                _hero.Position.X + frac.Position.X * heroSize.X,
                _hero.Position.Y + frac.Position.Y * heroSize.Y,
                frac.Size.X * heroSize.X,
                frac.Size.Y * heroSize.Y);

            int prevRows = _rowsVisible;
            int prevCols = _cols;
            int prevTop = _top;
            float prevCell = _cell;
            _cols = fit.Cols;
            _cell = fit.Cell;
            _rowsVisible = fit.Rows;
            EnsureGridVisible();

            if (prevRows != _rowsVisible || prevCols != _cols || prevTop != _top
                || Math.Abs(prevCell - _cell) > 0.5f || _grid.GetChildCount() == 0)
            {
                if (Current is WheelWindow w)
                {
                    PaintGrid(w);
                }
            }

            int cols = Math.Max(1, _cols);
            float gridW = cols * _cell + (cols - 1) * CellGap;
            float gridH = _rowsVisible * _cell + (_rowsVisible - 1) * CellGap;
            float gridX = content.Position.X + Math.Max(0f, (content.Size.X - (scrolled ? 14f : 0f) - gridW) * 0.5f);
            _grid.Position = new Vector2(gridX, content.Position.Y + Math.Max(0f, (content.Size.Y - gridH) * 0.5f));
            _grid.Size = new Vector2(gridW, gridH);

            int totalRows = Math.Max(1, (_lines.Count + cols - 1) / cols);
            bool needScroll = totalRows > _rowsVisible;

            if (_scrollTrack != null && GodotObject.IsInstanceValid(_scrollTrack))
            {
                _scrollTrack.Visible = needScroll;
                if (needScroll)
                {
                    const float barW = 10f;
                    _scrollTrack.Position = new Vector2(content.Position.X + content.Size.X - barW - 2f, content.Position.Y);
                    _scrollTrack.Size = new Vector2(barW, content.Size.Y);
                    float thumbH = Math.Max(16f, content.Size.Y * (_rowsVisible / (float)totalRows));
                    float travel = Math.Max(0f, content.Size.Y - thumbH);
                    float t = totalRows <= _rowsVisible ? 0f : _top / (float)(totalRows - _rowsVisible);
                    _scrollThumb.Position = new Vector2(1f, t * travel);
                    _scrollThumb.Size = new Vector2(barW - 2f, thumbH);
                }
            }

            if (_detail != null && GodotObject.IsInstanceValid(_detail) && _stage != null)
            {
                if (_detail.GetParent() != _stage)
                {
                    _detail.GetParent()?.RemoveChild(_detail);
                    _stage.AddChild(_detail);
                }

                _detail.Visible = true;
                float detailTop = _hero.Position.Y + heroSize.Y + 4f;
                float detailH = Math.Max(16f, Math.Min(fit.DetailRoom - 4f, stageH - detailTop - 2f));

                if (detailTop + detailH > stageH)
                {
                    detailTop = Math.Max(0f, stageH - detailH);
                }

                _detail.CustomMinimumSize = Vector2.Zero;
                _detail.Position = new Vector2(4f, detailTop);
                _detail.Size = new Vector2(Math.Max(8f, stageW - 8f), detailH);
                _detail.HorizontalAlignment = HorizontalAlignment.Center;
                _detail.VerticalAlignment = VerticalAlignment.Center;
                _detail.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                _detail.ClipText = false;
            }

        }

        private static void ParkDetailInColumn()
        {
            Control col = _content?.GetNodeOrNull<Control>("PadScreenCol");

            if (_detail == null || col == null || !GodotObject.IsInstanceValid(_detail))
            {
                return;
            }

            if (_detail.GetParent() != col)
            {
                _detail.GetParent()?.RemoveChild(_detail);
                col.AddChild(_detail);
            }

            _detail.CustomMinimumSize = Vector2.Zero;
        }
    }
}
