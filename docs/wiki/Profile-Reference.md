# Profile reference

Audit baseline: `main` `fc9e3588`, 2026-10-28.
These are source-discovered entry points, not new execution receipts.
Read the linked README/source before running a tool; dependencies, approvals,
output paths and destructive verbs differ. Generated output belongs outside
the checkout. A shell twin may explain a Windows-only limitation.

This is a source reference for scalar auto-properties in
[Profile.cs](../../godot/GUO/src/Configuration/Profile.cs), not a list of UI
labels or effective platform defaults. `(type default)` means the property has
no explicit initializer: false for bool, zero for numeric values, null for
string. Expressions are shown as written. Complex properties, fields and
custom accessors are outside this extraction; consult the complete source.
Account identity properties are intentionally omitted.

[PlatformDefaults.cs](../../godot/GUO/src/Configuration/PlatformDefaults.cs)
applies versioned desktop/mobile/web overrides after construction.
Saved profiles, migration and constraints can override or clamp these values.
For example, mobile uses larger/grid containers, full-size world, touch macro
row and low-power backgrounds; desktop also enables full-size world and saved
wheel zoom. `EnhancedHD` remains opt-in on desktop and mobile.
Prefer [Gameplay and Settings](Gameplay-and-Settings.md) for normal use.

| Property | Type | Base initializer | Source |
|---|---|---|---|
| `MaxJournalFiles` | `int` | `100` | [line 52](../../godot/GUO/src/Configuration/Profile.cs#L52) |
| `JournalFileWithSerial` | `bool` | `false` | [line 53](../../godot/GUO/src/Configuration/Profile.cs#L53) |
| `ProfileVersion` | `int` | `(type default)` | [line 64](../../godot/GUO/src/Configuration/Profile.cs#L64) |
| `ExperimentalWorldOverview` | `bool` | `(type default)` | [line 67](../../godot/GUO/src/Configuration/Profile.cs#L67) |
| `WorldOverviewStyle` | `int` | `(type default)` | [line 68](../../godot/GUO/src/Configuration/Profile.cs#L68) |
| `EnableSound` | `bool` | `true` | [line 72](../../godot/GUO/src/Configuration/Profile.cs#L72) |
| `SoundVolume` | `int` | `100` | [line 73](../../godot/GUO/src/Configuration/Profile.cs#L73) |
| `EnableMusic` | `bool` | `true` | [line 74](../../godot/GUO/src/Configuration/Profile.cs#L74) |
| `MusicVolume` | `int` | `100` | [line 75](../../godot/GUO/src/Configuration/Profile.cs#L75) |
| `EnableFootstepsSound` | `bool` | `true` | [line 76](../../godot/GUO/src/Configuration/Profile.cs#L76) |
| `EnableCombatMusic` | `bool` | `true` | [line 77](../../godot/GUO/src/Configuration/Profile.cs#L77) |
| `ReproduceSoundsInBackground` | `bool` | `(type default)` | [line 78](../../godot/GUO/src/Configuration/Profile.cs#L78) |
| `ContentAudioEnabled` | `bool` | `(type default)` | [line 80](../../godot/GUO/src/Configuration/Profile.cs#L80) |
| `ChatFont` | `byte` | `1` | [line 83](../../godot/GUO/src/Configuration/Profile.cs#L83) |
| `SpeechDelay` | `int` | `100` | [line 84](../../godot/GUO/src/Configuration/Profile.cs#L84) |
| `ScaleSpeechDelay` | `bool` | `true` | [line 85](../../godot/GUO/src/Configuration/Profile.cs#L85) |
| `SaveJournalToFile` | `bool` | `true` | [line 86](../../godot/GUO/src/Configuration/Profile.cs#L86) |
| `ForceUnicodeJournal` | `bool` | `(type default)` | [line 87](../../godot/GUO/src/Configuration/Profile.cs#L87) |
| `IgnoreAllianceMessages` | `bool` | `(type default)` | [line 88](../../godot/GUO/src/Configuration/Profile.cs#L88) |
| `IgnoreGuildMessages` | `bool` | `(type default)` | [line 89](../../godot/GUO/src/Configuration/Profile.cs#L89) |
| `EnabledSpellHue` | `bool` | `(type default)` | [line 110](../../godot/GUO/src/Configuration/Profile.cs#L110) |
| `EnabledSpellFormat` | `bool` | `(type default)` | [line 111](../../godot/GUO/src/Configuration/Profile.cs#L111) |
| `SpellDisplayFormat` | `string` | `"{power} [{spell}]"` | [line 112](../../godot/GUO/src/Configuration/Profile.cs#L112) |
| `EnabledCriminalActionQuery` | `bool` | `true` | [line 118](../../godot/GUO/src/Configuration/Profile.cs#L118) |
| `EnabledBeneficialCriminalActionQuery` | `bool` | `false` | [line 119](../../godot/GUO/src/Configuration/Profile.cs#L119) |
| `EnableStatReport` | `bool` | `true` | [line 120](../../godot/GUO/src/Configuration/Profile.cs#L120) |
| `EnableSkillReport` | `bool` | `true` | [line 121](../../godot/GUO/src/Configuration/Profile.cs#L121) |
| `UseOldStatusGump` | `bool` | `(type default)` | [line 122](../../godot/GUO/src/Configuration/Profile.cs#L122) |
| `StatusGumpBarMutuallyExclusive` | `bool` | `true` | [line 123](../../godot/GUO/src/Configuration/Profile.cs#L123) |
| `BackpackStyle` | `int` | `(type default)` | [line 124](../../godot/GUO/src/Configuration/Profile.cs#L124) |
| `HighlightGameObjects` | `bool` | `(type default)` | [line 125](../../godot/GUO/src/Configuration/Profile.cs#L125) |
| `HighlightMobilesByParalize` | `bool` | `true` | [line 126](../../godot/GUO/src/Configuration/Profile.cs#L126) |
| `HighlightMobilesByPoisoned` | `bool` | `true` | [line 127](../../godot/GUO/src/Configuration/Profile.cs#L127) |
| `HighlightMobilesByInvul` | `bool` | `true` | [line 128](../../godot/GUO/src/Configuration/Profile.cs#L128) |
| `ShowMobilesHP` | `bool` | `(type default)` | [line 129](../../godot/GUO/src/Configuration/Profile.cs#L129) |
| `MobileHPType` | `int` | `(type default)` | [line 130](../../godot/GUO/src/Configuration/Profile.cs#L130) |
| `MobileHPShowWhen` | `int` | `(type default)` | [line 131](../../godot/GUO/src/Configuration/Profile.cs#L131) |
| `DrawRoofs` | `bool` | `true` | [line 132](../../godot/GUO/src/Configuration/Profile.cs#L132) |
| `TreeToStumps` | `bool` | `(type default)` | [line 133](../../godot/GUO/src/Configuration/Profile.cs#L133) |
| `EnableCaveBorder` | `bool` | `(type default)` | [line 134](../../godot/GUO/src/Configuration/Profile.cs#L134) |
| `HideVegetation` | `bool` | `(type default)` | [line 135](../../godot/GUO/src/Configuration/Profile.cs#L135) |
| `FieldsType` | `int` | `(type default)` | [line 136](../../godot/GUO/src/Configuration/Profile.cs#L136) |
| `NoColorObjectsOutOfRange` | `bool` | `(type default)` | [line 137](../../godot/GUO/src/Configuration/Profile.cs#L137) |
| `UseCircleOfTransparency` | `bool` | `(type default)` | [line 138](../../godot/GUO/src/Configuration/Profile.cs#L138) |
| `CircleOfTransparencyRadius` | `int` | `Constants.MAX_CIRCLE_OF_TRANSPARENCY_RADIUS / 2` | [line 139](../../godot/GUO/src/Configuration/Profile.cs#L139) |
| `CircleOfTransparencyType` | `int` | `(type default)` | [line 140](../../godot/GUO/src/Configuration/Profile.cs#L140) |
| `VendorGumpHeight` | `int` | `60` | [line 141](../../godot/GUO/src/Configuration/Profile.cs#L141) |
| `DefaultScale` | `float` | `0.7f` | [line 142](../../godot/GUO/src/Configuration/Profile.cs#L142) |
| `EnableMousewheelScaleZoom` | `bool` | `true` | [line 143](../../godot/GUO/src/Configuration/Profile.cs#L143) |
| `SaveScaleAfterClose` | `bool` | `(type default)` | [line 144](../../godot/GUO/src/Configuration/Profile.cs#L144) |
| `RestoreScaleAfterUnpressCtrl` | `bool` | `(type default)` | [line 145](../../godot/GUO/src/Configuration/Profile.cs#L145) |
| `BandageSelfOld` | `bool` | `true` | [line 146](../../godot/GUO/src/Configuration/Profile.cs#L146) |
| `EnableDeathScreen` | `bool` | `true` | [line 147](../../godot/GUO/src/Configuration/Profile.cs#L147) |
| `EnableBlackWhiteEffect` | `bool` | `true` | [line 148](../../godot/GUO/src/Configuration/Profile.cs#L148) |
| `UseTooltip` | `bool` | `true` | [line 151](../../godot/GUO/src/Configuration/Profile.cs#L151) |
| `TooltipDelayBeforeDisplay` | `int` | `250` | [line 153](../../godot/GUO/src/Configuration/Profile.cs#L153) |
| `TooltipDisplayZoom` | `int` | `100` | [line 154](../../godot/GUO/src/Configuration/Profile.cs#L154) |
| `TooltipBackgroundOpacity` | `int` | `70` | [line 155](../../godot/GUO/src/Configuration/Profile.cs#L155) |
| `TooltipFont` | `byte` | `1` | [line 156](../../godot/GUO/src/Configuration/Profile.cs#L156) |
| `EnablePathfind` | `bool` | `(type default)` | [line 159](../../godot/GUO/src/Configuration/Profile.cs#L159) |
| `UseShiftToPathfind` | `bool` | `(type default)` | [line 160](../../godot/GUO/src/Configuration/Profile.cs#L160) |
| `AlwaysRun` | `bool` | `(type default)` | [line 161](../../godot/GUO/src/Configuration/Profile.cs#L161) |
| `AlwaysRunUnlessHidden` | `bool` | `(type default)` | [line 162](../../godot/GUO/src/Configuration/Profile.cs#L162) |
| `FastRotation` | `bool` | `(type default)` | [line 163](../../godot/GUO/src/Configuration/Profile.cs#L163) |
| `SmoothMovements` | `bool` | `true` | [line 164](../../godot/GUO/src/Configuration/Profile.cs#L164) |
| `HoldDownKeyTab` | `bool` | `true` | [line 165](../../godot/GUO/src/Configuration/Profile.cs#L165) |
| `HoldShiftForContext` | `bool` | `false` | [line 166](../../godot/GUO/src/Configuration/Profile.cs#L166) |
| `HoldShiftToSplitStack` | `bool` | `false` | [line 167](../../godot/GUO/src/Configuration/Profile.cs#L167) |
| `GameWindowLock` | `bool` | `(type default)` | [line 173](../../godot/GUO/src/Configuration/Profile.cs#L173) |
| `GameWindowFullSize` | `bool` | `true` | [line 174](../../godot/GUO/src/Configuration/Profile.cs#L174) |
| `WindowBorderless` | `bool` | `false` | [line 175](../../godot/GUO/src/Configuration/Profile.cs#L175) |
| `TopbarGumpIsMinimized` | `bool` | `(type default)` | [line 178](../../godot/GUO/src/Configuration/Profile.cs#L178) |
| `TopbarGumpIsDisabled` | `bool` | `(type default)` | [line 179](../../godot/GUO/src/Configuration/Profile.cs#L179) |
| `DualScreenEnabled` | `bool` | `true` | [line 182](../../godot/GUO/src/Configuration/Profile.cs#L182) |
| `DualScreenShelvePaperdoll` | `bool` | `(type default)` | [line 189](../../godot/GUO/src/Configuration/Profile.cs#L189) |
| `DualScreenShelveBackpack` | `bool` | `(type default)` | [line 190](../../godot/GUO/src/Configuration/Profile.cs#L190) |
| `DualScreenShelveStatus` | `bool` | `(type default)` | [line 191](../../godot/GUO/src/Configuration/Profile.cs#L191) |
| `DualScreenShelveJournal` | `bool` | `(type default)` | [line 192](../../godot/GUO/src/Configuration/Profile.cs#L192) |
| `DualScreenShelveOthers` | `bool` | `(type default)` | [line 193](../../godot/GUO/src/Configuration/Profile.cs#L193) |
| `DualScreenScale` | `int` | `(type default)` | [line 194](../../godot/GUO/src/Configuration/Profile.cs#L194) |
| `DualScreenScalePercent` | `int` | `(type default)` | [line 198](../../godot/GUO/src/Configuration/Profile.cs#L198) |
| `OneScreenPanel` | `int` | `(type default)` | [line 206](../../godot/GUO/src/Configuration/Profile.cs#L206) |
| `OneScreenDrawerSide` | `int` | `(type default)` | [line 207](../../godot/GUO/src/Configuration/Profile.cs#L207) |
| `OneScreenSquareLayout` | `int` | `(type default)` | [line 208](../../godot/GUO/src/Configuration/Profile.cs#L208) |
| `UseAlternativeLights` | `bool` | `(type default)` | [line 209](../../godot/GUO/src/Configuration/Profile.cs#L209) |
| `UseCustomLightLevel` | `bool` | `(type default)` | [line 210](../../godot/GUO/src/Configuration/Profile.cs#L210) |
| `LightLevel` | `byte` | `(type default)` | [line 211](../../godot/GUO/src/Configuration/Profile.cs#L211) |
| `LightLevelType` | `int` | `(type default)` | [line 212](../../godot/GUO/src/Configuration/Profile.cs#L212) |
| `UseColoredLights` | `bool` | `true` | [line 213](../../godot/GUO/src/Configuration/Profile.cs#L213) |
| `UseDarkNights` | `bool` | `(type default)` | [line 214](../../godot/GUO/src/Configuration/Profile.cs#L214) |
| `CloseHealthBarType` | `int` | `(type default)` | [line 215](../../godot/GUO/src/Configuration/Profile.cs#L215) |
| `ActivateChatAfterEnter` | `bool` | `(type default)` | [line 216](../../godot/GUO/src/Configuration/Profile.cs#L216) |
| `ActivateChatAdditionalButtons` | `bool` | `true` | [line 217](../../godot/GUO/src/Configuration/Profile.cs#L217) |
| `ActivateChatShiftEnterSupport` | `bool` | `true` | [line 218](../../godot/GUO/src/Configuration/Profile.cs#L218) |
| `UseObjectsFading` | `bool` | `true` | [line 219](../../godot/GUO/src/Configuration/Profile.cs#L219) |
| `HoldDownKeyAltToCloseAnchored` | `bool` | `true` | [line 220](../../godot/GUO/src/Configuration/Profile.cs#L220) |
| `CloseAllAnchoredGumpsInGroupWithRightClick` | `bool` | `false` | [line 221](../../godot/GUO/src/Configuration/Profile.cs#L221) |
| `HoldAltToMoveGumps` | `bool` | `(type default)` | [line 222](../../godot/GUO/src/Configuration/Profile.cs#L222) |
| `HideScreenshotStoredInMessage` | `bool` | `(type default)` | [line 224](../../godot/GUO/src/Configuration/Profile.cs#L224) |
| `CastSpellsByOneClick` | `bool` | `(type default)` | [line 227](../../godot/GUO/src/Configuration/Profile.cs#L227) |
| `BuffBarTime` | `bool` | `(type default)` | [line 228](../../godot/GUO/src/Configuration/Profile.cs#L228) |
| `FastSpellsAssign` | `bool` | `(type default)` | [line 229](../../godot/GUO/src/Configuration/Profile.cs#L229) |
| `AutoOpenDoors` | `bool` | `(type default)` | [line 230](../../godot/GUO/src/Configuration/Profile.cs#L230) |
| `SmoothDoors` | `bool` | `(type default)` | [line 231](../../godot/GUO/src/Configuration/Profile.cs#L231) |
| `AutoOpenCorpses` | `bool` | `(type default)` | [line 232](../../godot/GUO/src/Configuration/Profile.cs#L232) |
| `AutoOpenCorpseRange` | `int` | `2` | [line 233](../../godot/GUO/src/Configuration/Profile.cs#L233) |
| `CorpseOpenOptions` | `int` | `3` | [line 234](../../godot/GUO/src/Configuration/Profile.cs#L234) |
| `SkipEmptyCorpse` | `bool` | `(type default)` | [line 235](../../godot/GUO/src/Configuration/Profile.cs#L235) |
| `DisableDefaultHotkeys` | `bool` | `(type default)` | [line 236](../../godot/GUO/src/Configuration/Profile.cs#L236) |
| `DisableArrowBtn` | `bool` | `(type default)` | [line 237](../../godot/GUO/src/Configuration/Profile.cs#L237) |
| `DisableTabBtn` | `bool` | `(type default)` | [line 238](../../godot/GUO/src/Configuration/Profile.cs#L238) |
| `DisableCtrlQWBtn` | `bool` | `(type default)` | [line 239](../../godot/GUO/src/Configuration/Profile.cs#L239) |
| `DisableAutoMove` | `bool` | `(type default)` | [line 240](../../godot/GUO/src/Configuration/Profile.cs#L240) |
| `EnableDragSelect` | `bool` | `(type default)` | [line 241](../../godot/GUO/src/Configuration/Profile.cs#L241) |
| `DragSelectModifierKey` | `int` | `(type default)` | [line 242](../../godot/GUO/src/Configuration/Profile.cs#L242) |
| `OverrideContainerLocation` | `bool` | `(type default)` | [line 243](../../godot/GUO/src/Configuration/Profile.cs#L243) |
| `FitContainerPlacement` | `bool` | `(type default)` | [line 247](../../godot/GUO/src/Configuration/Profile.cs#L247) |
| `GridContainers` | `bool` | `(type default)` | [line 251](../../godot/GUO/src/Configuration/Profile.cs#L251) |
| `GridContainerSlotSize` | `int` | `44` | [line 255](../../godot/GUO/src/Configuration/Profile.cs#L255) |
| `TouchMacroRow` | `bool` | `(type default)` | [line 261](../../godot/GUO/src/Configuration/Profile.cs#L261) |
| `TouchMacroSlots` | `string` | `"m:nearest,m:attack,m:next,m:last,m:bandage,m:war"` | [line 265](../../godot/GUO/src/Configuration/Profile.cs#L265) |
| `TouchBarSlots` | `string` | `GUO.Input.Touch.TouchGumpBar.DefaultSlots` | [line 269](../../godot/GUO/src/Configuration/Profile.cs#L269) |
| `TouchSayBank` | `string` | `"bank"` | [line 273](../../godot/GUO/src/Configuration/Profile.cs#L273) |
| `TouchSayGuards` | `string` | `"guards"` | [line 274](../../godot/GUO/src/Configuration/Profile.cs#L274) |
| `TouchSayFollow` | `string` | `"all follow me"` | [line 275](../../godot/GUO/src/Configuration/Profile.cs#L275) |
| `TouchSayStop` | `string` | `"all stop"` | [line 276](../../godot/GUO/src/Configuration/Profile.cs#L276) |
| `TouchBarAlts` | `string` | `(type default)` | [line 281](../../godot/GUO/src/Configuration/Profile.cs#L281) |
| `TouchBarWords` | `string` | `""` | [line 286](../../godot/GUO/src/Configuration/Profile.cs#L286) |
| `ModernGumpsOff` | `bool` | `(type default)` | [line 290](../../godot/GUO/src/Configuration/Profile.cs#L290) |
| `TouchVibrate` | `bool` | `(type default)` | [line 294](../../godot/GUO/src/Configuration/Profile.cs#L294) |
| `TouchReduceMotion` | `bool` | `(type default)` | [line 295](../../godot/GUO/src/Configuration/Profile.cs#L295) |
| `TouchChevronInset` | `int` | `(type default)` | [line 300](../../godot/GUO/src/Configuration/Profile.cs#L300) |
| `FlickUp` | `int` | `1` | [line 305](../../godot/GUO/src/Configuration/Profile.cs#L305) |
| `DebugShowTouches` | `bool` | `(type default)` | [line 309](../../godot/GUO/src/Configuration/Profile.cs#L309) |
| `ShowWindowHandles` | `bool` | `(type default)` | [line 315](../../godot/GUO/src/Configuration/Profile.cs#L315) |
| `CompanionTabs` | `bool` | `(type default)` | [line 320](../../godot/GUO/src/Configuration/Profile.cs#L320) |
| `MobileWindowControls` | `bool` | `(type default)` | [line 325](../../godot/GUO/src/Configuration/Profile.cs#L325) |
| `FlickDown` | `int` | `2` | [line 327](../../godot/GUO/src/Configuration/Profile.cs#L327) |
| `FlickLeft` | `int` | `3` | [line 328](../../godot/GUO/src/Configuration/Profile.cs#L328) |
| `FlickRight` | `int` | `4` | [line 329](../../godot/GUO/src/Configuration/Profile.cs#L329) |
| `CanvasBackgroundMode` | `string` | `"builtin-grey"` | [line 336](../../godot/GUO/src/Configuration/Profile.cs#L336) |
| `CanvasBackgroundPath` | `string` | `""` | [line 337](../../godot/GUO/src/Configuration/Profile.cs#L337) |
| `CanvasBackgroundFps` | `int` | `12` | [line 338](../../godot/GUO/src/Configuration/Profile.cs#L338) |
| `CanvasBackgroundLowPower` | `bool` | `(type default)` | [line 339](../../godot/GUO/src/Configuration/Profile.cs#L339) |
| `ScreenSaver` | `bool` | `(type default)` | [line 343](../../godot/GUO/src/Configuration/Profile.cs#L343) |
| `ScreenSaverMinutes` | `int` | `10` | [line 344](../../godot/GUO/src/Configuration/Profile.cs#L344) |
| `ScreenSaverSleepMinutes` | `int` | `(type default)` | [line 348](../../godot/GUO/src/Configuration/Profile.cs#L348) |
| `ScreenSaverChoice` | `string` | `"effects"` | [line 351](../../godot/GUO/src/Configuration/Profile.cs#L351) |
| `GamepadLayout` | `string` | `"auto"` | [line 355](../../godot/GUO/src/Configuration/Profile.cs#L355) |
| `Gamepad` | `bool` | `true` | [line 359](../../godot/GUO/src/Configuration/Profile.cs#L359) |
| `OverrideContainerLocationSetting` | `int` | `(type default)` | [line 360](../../godot/GUO/src/Configuration/Profile.cs#L360) |
| `HueContainerGumps` | `bool` | `true` | [line 363](../../godot/GUO/src/Configuration/Profile.cs#L363) |
| `DragSelectHumanoidsOnly` | `bool` | `(type default)` | [line 364](../../godot/GUO/src/Configuration/Profile.cs#L364) |
| `DragSelectHostileOnly` | `bool` | `(type default)` | [line 365](../../godot/GUO/src/Configuration/Profile.cs#L365) |
| `DragSelectStartX` | `int` | `100` | [line 366](../../godot/GUO/src/Configuration/Profile.cs#L366) |
| `DragSelectStartY` | `int` | `100` | [line 367](../../godot/GUO/src/Configuration/Profile.cs#L367) |
| `DragSelectAsAnchor` | `bool` | `false` | [line 368](../../godot/GUO/src/Configuration/Profile.cs#L368) |
| `NameOverheadToggled` | `bool` | `false` | [line 370](../../godot/GUO/src/Configuration/Profile.cs#L370) |
| `NameOverheadShowGump` | `bool` | `true` | [line 371](../../godot/GUO/src/Configuration/Profile.cs#L371) |
| `NameOverheadShowHpBar` | `bool` | `true` | [line 372](../../godot/GUO/src/Configuration/Profile.cs#L372) |
| `ShowTargetRangeIndicator` | `bool` | `(type default)` | [line 373](../../godot/GUO/src/Configuration/Profile.cs#L373) |
| `PartyInviteGump` | `bool` | `(type default)` | [line 374](../../godot/GUO/src/Configuration/Profile.cs#L374) |
| `CustomBarsToggled` | `bool` | `(type default)` | [line 375](../../godot/GUO/src/Configuration/Profile.cs#L375) |
| `CBBlackBGToggled` | `bool` | `(type default)` | [line 376](../../godot/GUO/src/Configuration/Profile.cs#L376) |
| `ShowInfoBar` | `bool` | `(type default)` | [line 378](../../godot/GUO/src/Configuration/Profile.cs#L378) |
| `InfoBarHighlightType` | `int` | `(type default)` | [line 379](../../godot/GUO/src/Configuration/Profile.cs#L379) |
| `CounterBarEnabled` | `bool` | `(type default)` | [line 381](../../godot/GUO/src/Configuration/Profile.cs#L381) |
| `CounterBarHighlightOnChange` | `bool` | `true` | [line 382](../../godot/GUO/src/Configuration/Profile.cs#L382) |
| `CounterBarHighlightOnAmount` | `bool` | `(type default)` | [line 383](../../godot/GUO/src/Configuration/Profile.cs#L383) |
| `CounterBarDisplayAbbreviatedAmount` | `bool` | `(type default)` | [line 384](../../godot/GUO/src/Configuration/Profile.cs#L384) |
| `CounterBarAbbreviatedAmount` | `int` | `1000` | [line 385](../../godot/GUO/src/Configuration/Profile.cs#L385) |
| `CounterBarHighlightAmount` | `int` | `5` | [line 386](../../godot/GUO/src/Configuration/Profile.cs#L386) |
| `CounterBarCellSize` | `int` | `40` | [line 387](../../godot/GUO/src/Configuration/Profile.cs#L387) |
| `ShowSkillsChangedMessage` | `bool` | `true` | [line 389](../../godot/GUO/src/Configuration/Profile.cs#L389) |
| `ShowSkillsChangedDeltaValue` | `int` | `1` | [line 390](../../godot/GUO/src/Configuration/Profile.cs#L390) |
| `ShowStatsChangedMessage` | `bool` | `true` | [line 391](../../godot/GUO/src/Configuration/Profile.cs#L391) |
| `ShadowsEnabled` | `bool` | `true` | [line 394](../../godot/GUO/src/Configuration/Profile.cs#L394) |
| `ShadowsStatics` | `bool` | `true` | [line 395](../../godot/GUO/src/Configuration/Profile.cs#L395) |
| `TerrainShadowsLevel` | `int` | `15` | [line 396](../../godot/GUO/src/Configuration/Profile.cs#L396) |
| `AuraUnderFeetType` | `int` | `(type default)` | [line 397](../../godot/GUO/src/Configuration/Profile.cs#L397) |
| `AuraOnMouse` | `bool` | `true` | [line 398](../../godot/GUO/src/Configuration/Profile.cs#L398) |
| `AnimatedWaterEffect` | `bool` | `false` | [line 399](../../godot/GUO/src/Configuration/Profile.cs#L399) |
| `PartyAura` | `bool` | `(type default)` | [line 401](../../godot/GUO/src/Configuration/Profile.cs#L401) |
| `UseXBR` | `bool` | `true` | [line 403](../../godot/GUO/src/Configuration/Profile.cs#L403) |
| `EnhancedHD` | `bool` | `false` | [line 409](../../godot/GUO/src/Configuration/Profile.cs#L409) |
| `HdFilter` | `HdFilterMode` | `HdFilterMode.Auto` | [line 410](../../godot/GUO/src/Configuration/Profile.cs#L410) |
| `RealtimeLights` | `bool` | `false` | [line 416](../../godot/GUO/src/Configuration/Profile.cs#L416) |
| `SpellLighting` | `bool` | `false` | [line 419](../../godot/GUO/src/Configuration/Profile.cs#L419) |
| `AmbientFog` | `bool` | `false` | [line 421](../../godot/GUO/src/Configuration/Profile.cs#L421) |
| `AmbientLeaves` | `bool` | `false` | [line 422](../../godot/GUO/src/Configuration/Profile.cs#L422) |
| `AmbientDust` | `bool` | `false` | [line 423](../../godot/GUO/src/Configuration/Profile.cs#L423) |
| `AmbientParticleDensity` | `int` | `50` | [line 461](../../godot/GUO/src/Configuration/Profile.cs#L461) |
| `AmbientParticleCap` | `int` | `128` | [line 462](../../godot/GUO/src/Configuration/Profile.cs#L462) |
| `WaterStyle` | `string` | `"classic"` | [line 466](../../godot/GUO/src/Configuration/Profile.cs#L466) |
| `ShardWaterConsent` | `string` | `""` | [line 469](../../godot/GUO/src/Configuration/Profile.cs#L469) |
| `ShardWaterStyle` | `string` | `""` | [line 470](../../godot/GUO/src/Configuration/Profile.cs#L470) |
| `HideChatGradient` | `bool` | `false` | [line 472](../../godot/GUO/src/Configuration/Profile.cs#L472) |
| `StandardSkillsGump` | `bool` | `true` | [line 474](../../godot/GUO/src/Configuration/Profile.cs#L474) |
| `ShowNewMobileNameIncoming` | `bool` | `true` | [line 476](../../godot/GUO/src/Configuration/Profile.cs#L476) |
| `ShowNewCorpseNameIncoming` | `bool` | `true` | [line 477](../../godot/GUO/src/Configuration/Profile.cs#L477) |
| `GrabBagSerial` | `uint` | `(type default)` | [line 479](../../godot/GUO/src/Configuration/Profile.cs#L479) |
| `GridLootType` | `int` | `(type default)` | [line 481](../../godot/GUO/src/Configuration/Profile.cs#L481) |
| `ReduceFPSWhenInactive` | `bool` | `true` | [line 483](../../godot/GUO/src/Configuration/Profile.cs#L483) |
| `OverrideAllFonts` | `bool` | `(type default)` | [line 485](../../godot/GUO/src/Configuration/Profile.cs#L485) |
| `OverrideAllFontsIsUnicode` | `bool` | `true` | [line 486](../../godot/GUO/src/Configuration/Profile.cs#L486) |
| `SallosEasyGrab` | `bool` | `(type default)` | [line 488](../../godot/GUO/src/Configuration/Profile.cs#L488) |
| `JournalDarkMode` | `bool` | `(type default)` | [line 490](../../godot/GUO/src/Configuration/Profile.cs#L490) |
| `ContainersScale` | `byte` | `100` | [line 492](../../godot/GUO/src/Configuration/Profile.cs#L492) |
| `ScaleItemsInsideContainers` | `bool` | `(type default)` | [line 494](../../godot/GUO/src/Configuration/Profile.cs#L494) |
| `DoubleClickToLootInsideContainers` | `bool` | `(type default)` | [line 496](../../godot/GUO/src/Configuration/Profile.cs#L496) |
| `UseLargeContainerGumps` | `bool` | `false` | [line 498](../../godot/GUO/src/Configuration/Profile.cs#L498) |
| `RelativeDragAndDropItems` | `bool` | `(type default)` | [line 500](../../godot/GUO/src/Configuration/Profile.cs#L500) |
| `HighlightContainerWhenSelected` | `bool` | `(type default)` | [line 502](../../godot/GUO/src/Configuration/Profile.cs#L502) |
| `UseNewTargetSystem` | `bool` | `true` | [line 504](../../godot/GUO/src/Configuration/Profile.cs#L504) |
| `UseKrEquipUnequipPacket` | `bool` | `(type default)` | [line 505](../../godot/GUO/src/Configuration/Profile.cs#L505) |
| `ShowHouseContent` | `bool` | `(type default)` | [line 506](../../godot/GUO/src/Configuration/Profile.cs#L506) |
| `SaveHealthbars` | `bool` | `(type default)` | [line 507](../../godot/GUO/src/Configuration/Profile.cs#L507) |
| `TextFading` | `bool` | `true` | [line 508](../../godot/GUO/src/Configuration/Profile.cs#L508) |
| `UseSmoothBoatMovement` | `bool` | `false` | [line 510](../../godot/GUO/src/Configuration/Profile.cs#L510) |
| `IgnoreStaminaCheck` | `bool` | `false` | [line 512](../../godot/GUO/src/Configuration/Profile.cs#L512) |
| `ShowJournalClient` | `bool` | `true` | [line 514](../../godot/GUO/src/Configuration/Profile.cs#L514) |
| `ShowJournalObjects` | `bool` | `true` | [line 515](../../godot/GUO/src/Configuration/Profile.cs#L515) |
| `ShowJournalSystem` | `bool` | `true` | [line 516](../../godot/GUO/src/Configuration/Profile.cs#L516) |
| `ShowJournalGuildAlly` | `bool` | `true` | [line 517](../../godot/GUO/src/Configuration/Profile.cs#L517) |
| `WorldMapWidth` | `int` | `400` | [line 519](../../godot/GUO/src/Configuration/Profile.cs#L519) |
| `WorldMapHeight` | `int` | `400` | [line 520](../../godot/GUO/src/Configuration/Profile.cs#L520) |
| `WorldMapFont` | `int` | `3` | [line 521](../../godot/GUO/src/Configuration/Profile.cs#L521) |
| `WorldMapFlipMap` | `bool` | `true` | [line 522](../../godot/GUO/src/Configuration/Profile.cs#L522) |
| `WorldMapTopMost` | `bool` | `(type default)` | [line 523](../../godot/GUO/src/Configuration/Profile.cs#L523) |
| `WorldMapFreeView` | `bool` | `(type default)` | [line 524](../../godot/GUO/src/Configuration/Profile.cs#L524) |
| `WorldMapShowParty` | `bool` | `true` | [line 525](../../godot/GUO/src/Configuration/Profile.cs#L525) |
| `WorldMapZoomIndex` | `int` | `4` | [line 526](../../godot/GUO/src/Configuration/Profile.cs#L526) |
| `WorldMapShowCoordinates` | `bool` | `true` | [line 527](../../godot/GUO/src/Configuration/Profile.cs#L527) |
| `WorldMapShowMouseCoordinates` | `bool` | `true` | [line 528](../../godot/GUO/src/Configuration/Profile.cs#L528) |
| `WorldMapShowSextantCoordinates` | `bool` | `false` | [line 529](../../godot/GUO/src/Configuration/Profile.cs#L529) |
| `WorldMapShowMobiles` | `bool` | `true` | [line 530](../../godot/GUO/src/Configuration/Profile.cs#L530) |
| `WorldMapShowPlayerName` | `bool` | `true` | [line 531](../../godot/GUO/src/Configuration/Profile.cs#L531) |
| `WorldMapShowPlayerBar` | `bool` | `true` | [line 532](../../godot/GUO/src/Configuration/Profile.cs#L532) |
| `WorldMapShowGroupName` | `bool` | `true` | [line 533](../../godot/GUO/src/Configuration/Profile.cs#L533) |
| `WorldMapShowGroupBar` | `bool` | `true` | [line 534](../../godot/GUO/src/Configuration/Profile.cs#L534) |
| `WorldMapShowMarkers` | `bool` | `true` | [line 535](../../godot/GUO/src/Configuration/Profile.cs#L535) |
| `WorldMapShowMarkersNames` | `bool` | `true` | [line 536](../../godot/GUO/src/Configuration/Profile.cs#L536) |
| `WorldMapShowMultis` | `bool` | `true` | [line 537](../../godot/GUO/src/Configuration/Profile.cs#L537) |
| `WorldMapHiddenMarkerFiles` | `string` | `string.Empty` | [line 538](../../godot/GUO/src/Configuration/Profile.cs#L538) |
| `WorldMapHiddenZoneFiles` | `string` | `string.Empty` | [line 539](../../godot/GUO/src/Configuration/Profile.cs#L539) |
| `WorldMapShowGridIfZoomed` | `bool` | `true` | [line 540](../../godot/GUO/src/Configuration/Profile.cs#L540) |
| `WorldMapAllowPositionalTarget` | `bool` | `false` | [line 541](../../godot/GUO/src/Configuration/Profile.cs#L541) |
| `ShowDPSWithDamageNumbers` | `bool` | `true` | [line 542](../../godot/GUO/src/Configuration/Profile.cs#L542) |
| `UseAlternateJournal` | `bool` | `(type default)` | [line 547](../../godot/GUO/src/Configuration/Profile.cs#L547) |
| `OverheadPartyMessages` | `bool` | `(type default)` | [line 579](../../godot/GUO/src/Configuration/Profile.cs#L579) |
