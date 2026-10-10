// SPDX-License-Identifier: BSD-2-Clause

namespace GUO.Game.UI.Gumps
{
    internal enum GumpType
    {
        None,

        Buff,
        Container,
        CounterBar,
        HealthBar,
        InfoBar,
        Journal,
        MacroButton,
        MiniMap,
        PaperDoll,
        SkillMenu,
        SpellBook,
        StatusGump,
        TipNotice,
        AbilityButton,
        SpellButton,
        SkillButton,
        RacialButton,
        WorldMap,

        Debug,
        NetStats,

        NameOverHeadHandler,

        // PORT DEVIATION (GUO): in-game authoring gumps have no upstream
        // counterpart; the enum grows with them (SplatPlacer here).
        SplatPlacer
    }
}