# Player commands and controller helpers

Research snapshot: 2026-09-27. Scope: GUO player actions, mobile helpers, future browser helpers, and Steam Deck/controller access. This is a design backlog, not a claim of implemented controller support.

Build a shared, searchable action catalog around the existing MacroManager and GameActions. Give immediate access to movement, interaction, targeting, cancellation, and a few character-specific actions. Put the long tail in contextual menus and favorites. The difficult part is completing targeting, inventory, gumps, and text entry with each input device—not exposing more macro buttons.

## Evidence and prioritization

Priorities below are hypotheses based on core play loops and existing client actions, not measured command frequencies. No player telemetry, surveys, or comparative device playtests were collected for this research. P0 means needed to complete basic play or urgent interaction; P1 means frequent within a character's role; P2 means situational. A healer's or tamer's P1 favorites should become their immediate shortcuts.

The implementation index is grounded in [MacroManager](../godot/GUO/src/Game/Managers/MacroManager.cs), including its MacroType and MacroSubType enums. Official UO documentation establishes vocabulary, but individual shards and eras can change availability and behavior. A listed macro handler means code exists, not that its complete flow has passed a device test.

## Current platform support

| Platform | Repository evidence | Controller/helper position |
| --- | --- | --- |
| Windows | README reports local-shard play; GodotInput handles key, mouse motion, and mouse button events | No native joypad dispatch found. External keyboard/mouse emulation is a possible fallback, not verified full controller support. |
| Android | Debug build documented on one device; TouchInput provides gestures and keyboard support | Touch helpers exist. Bluetooth/USB controller handling is not implemented in the audited input path. Device recognition alone will not invoke player actions. |
| Steam Deck / Linux | ADR-0018 records export, push, process startup, and Vulkan initialization; startup stopped because client data was absent | Login and Game-mode play remain unverified in that record. Documentation proposes trackpad mouse, R2 left click, L2 right click, and Steam+X keyboard. No native gamepad or Steam Input API integration found. |
| Web | ADR-0008 records rejected C# export and no rendered browser frame | No playable GUO web client to test. A browser command-reference/layout prototype is feasible separately; live control would need an explicit bridge and architecture. |
| iOS / macOS | No device validation established by this audit | Treat as unvalidated targets, not extensions of Android/Windows test results. |

Godot supports controllers across desktop, mobile, and web, but its desktop SDL3 backend differs from Android/Web handling. Mapping success on desktop does not establish mobile/browser behavior. [Godot controller documentation](https://docs.godotengine.org/en/stable/tutorials/inputs/controllers_gamepads_joysticks.html)

Godot's current stable web documentation still excludes Godot 4 C# exports. GUO additionally needs browser-compatible data access and networking, as recorded in ADR-0008. [Godot web export](https://docs.godotengine.org/en/stable/tutorials/export/exporting_for_web.html), [local web ADR](architecture/ADR-0008-web-target.md)

### Existing touch work to reuse

[TouchInput](../godot/GUO/src/Input/Touch/TouchInput.cs) translates tap, double tap, world hold/swipe, item drag, gump long press, and pinch into existing input. [TouchGumpBar](../godot/GUO/src/Input/Touch/TouchGumpBar.cs) exposes Paperdoll, Backpack, Journal, Map, Chat, and Options. During targeting, Chat/Options become Self/Cancel. Its optional macro row already offers Target next, Attack last, Last target, Last object, Bandage self, and War/Peace, with automatic opening on entering war mode unless hidden by the player.

[BackButton](../godot/GUO/src/Input/Touch/BackButton.cs) currently closes the top eligible gump, then hides the keyboard, then asks to quit in-world. It does not first cancel an active target. Unify this with a deliberate contextual Back policy.

Documentation discrepancy: [Steam Deck controls](steamdeck.md#7-controls) says the touch layer is not compiled for Linux. Current source includes it without a matching exclusion in GUO.csproj and Bootstrap/Main supports desktop `--touch`. This is not proof of working Deck touch; that flag also enables mouse-to-touch emulation. Verify actual Linux events before revising support claims.

## Indexed player actions

IDs are proposed stable catalog keys. “Route” names existing code operations where available; proposed adapters are explicit. No universal keyboard shortcuts are assumed because profiles can remap them.

| ID | Priority / context | Player intent | Existing route or required adapter | Suggested helper |
| --- | --- | --- | --- | --- |
| move.direction | P0 / world | Walk in eight directions | Movement adapter using existing walking path; Walk macro offers directions | Left stick; optional touch movement region |
| move.run | P0 / world | Walk/run choice | Existing movement semantics; AlwaysRun toggle | Explicit run control or configurable stick threshold |
| interact.use | P0 / world | Open/use object or door | Double-click path; UseSelectedTarget; OpenDoor | Interact button with named selection |
| interact.inspect | P0 / world | Read name/properties/context | Existing pointer/context paths; AllNames / NamesOnOff | Tap selection; inspect/context button |
| target.choose | P0 / target cursor | Answer a spell/item target | Existing TargetManager and pointer path | Highlight candidate then confirm |
| target.cancel | P0 / target cursor | Abort pending target | TargetManager.CancelTarget, already in touch bar | Persistent Cancel; controller Back |
| target.self | P0 / target cursor | Target own character | TargetSelf; existing touch Self | Dedicated target-mode shortcut |
| target.last | P0 / combat | Reuse last target | LastTarget | Show target name; explicit activation |
| target.cycle | P0 / combat | Select next/previous/nearest | TargetNext; SelectNext/Previous/Nearest with filters | Bumpers; hostile/party/follower selector |
| combat.attack | P0 / combat | Attack selected or previous opponent | AttackSelectedTarget / AttackLast | Separate labeled attack action |
| combat.mode | P0 / combat | War/peace | WarPeace | Visible state toggle |
| item.repeat | P1 / gathering, utility | Use last object/tool | LastObject | Favorite slot |
| item.use | P1 / role | Use chosen object or hand item | UseObject / UseItemInHand | Item-bound favorite with missing-item feedback |
| heal.bandage | P1 / healer, melee | Bandage self or target | BandageSelf / BandageTarget | Immediate self slot; targeted variant |
| heal.potion | P1 / combat | Heal, cure, refresh | UsePotion and supported subtypes | Three configurable favorites or short radial |
| magic.cast | P1 / caster | Cast selected spell | CastSpell + subtype | Spell favorites; retain target cursor |
| magic.repeat | P1 / caster | Repeat spell | LastSpell | Optional slot, not implicit repeat loop |
| skill.use | P1 / role | Hiding, meditation, taming, etc. | UseSkill + subtype; LastSkill | Role-specific favorites |
| combat.ability | P1 / melee | Primary/secondary weapon ability | PrimaryAbility / SecondaryAbility | Two slots with state feedback |
| equipment.swap | P1 / combat | Arm/disarm or restore weapon | ArmDisarm / EquipLastWeapon | Equipment wheel |
| inventory.open | P0 / all | Open backpack | Open + Backpack; touch bar | Always reachable |
| inventory.move | P0 / containers | Transfer an item or quantity | Existing lift/drop path; new select-item/destination adapter | Move-to sheet; quantity picker; retain drag |
| inventory.loot | P1 / combat | Take a selected item | Grab / SetGrabBag require semantics validation | Explicit selected-item action; no assumed loot-all |
| character.open | P0 / all | Equipment and status | Open + Paperdoll / Status | Menu or favorite |
| journal.open | P0 / all | Read server feedback | Open + Journal | Unread indicator; quick return |
| map.open | P0 / travel | Navigate map | Open + WorldMap / Overview | Menu button; zoom controls |
| chat.compose | P0 / social | Chat or enter command | System chat and existing speech paths | OS keyboard + channel selector |
| ui.back | P0 / all | Cancel current interaction | New shared context policy over existing handlers | Same meaning across touch/controller |
| ui.navigate | P0 / gumps | Select, scroll, confirm | New navigation adapter for custom gumps | D-pad focus plus pointer fallback |
| ui.settings | P0 / all | Configure controls/accessibility | Open + Configuration; new binding editor | Remap and restore defaults |
| ui.books | P1 / role | Skills, spellbooks, party, quests | Open + matching subtype | Searchable menu |
| view.adjust | P2 / exploration | Zoom, roof visibility, names | Zoom / ToggleDrawRoofs / NamesOnOff | View submenu |
| travel.mount | P1 / travel | Mount/dismount | Existing object/player interaction; new helper needs validation | Explicit role favorite |
| session.quit | P2 / session | Leave session | QuitGame / existing quit dialog | Menu with confirmation |

Target cycling must distinguish selecting a candidate from answering a pending target or attacking. MacroManager's filtered selection supports Hostile, Party, Follower, Object, and Mobile; assess each handler's effects before binding it. Ground targeting still needs a pointer/reticle. Do not assume a mobile-only selector can place a field spell.

## Spoken commands and shard profiles

These are server-interpreted speech, not local client commands. Preserve editable text and speech channel per shard. Do not silently translate command payloads with UI labels.

| Catalog group | Priority | Starter command / intent | Helper behavior |
| --- | --- | --- | --- |
| npc.bank | P1 / town | `bank` | Open nearby banking interaction |
| npc.trade | P1 / town | `vendor buy`, `vendor sell` | Trade sheet, then normal server UI |
| npc.stable | P1 / tamer | `stable`, `claim list` | Pet management menu |
| pet.follow | P1 / tamer | `all follow me` | Accessible pet favorite |
| pet.stop | P1 / tamer | `all stop` | Immediate pet stop action |
| pet.stay | P1 / tamer | `all stay` | Pet wheel |
| help.guards | P1 / emergency | `guards` through Yell | Optional quick slot; only useful where shard rules allow |
| pet.attack | P1 / tamer | Named pet attack order | Editable payload; explicit target step |
| house.manage | P2 / housing | Lock down, secure, release, eject | Context menu with privilege feedback |
| boat.navigate | P2 / sailing | Direction, turn, stop | Separate sailing context; prominent Stop |
| shard.custom | P2 / shard | Shard-specific help, travel, status | User-authored entries; never assume prefixes |

Official servers document these speech families and context menus; shard rules must be checked separately. [UO NPC and command reference](https://uo.com/wiki/ultima-online-wiki/beginning-the-adventure/communicating-with-npcs-and-other-commands/)

Pet recall during combat may require a stop order before follow/come. Make that an explained, shard-tested option rather than an invisible automatic chain. [UO pet ownership](https://uo.com/wiki/ultima-online-wiki/skills/animal-taming/pets-ownership/)

Keep pet release/transfer, house release, and similar consequential actions out of default rapid-access slots; expose their existing confirmation flows. Crafting and trading need navigation of the server's gumps, not merely speech buttons.

## Shared input design

Add an opt-in adapter around the ported logic, preserving Classic behavior and the parity-first architecture. Use the existing touch macro invocation pattern as the starting point.

Each catalog entry should carry: stable ID, localized label, category, execution route, parameters, allowed contexts, target kind (none/self/mobile/item/ground), availability reason, repeat policy, shard capability, and default placement. Store bindings separately by device, character, and shard. Do not persist raw MacroType numeric values as the public catalog contract.

Resolve input once through a context stack: modal/text entry → pending target → container/gump → world. Back should dismiss the active modal or text entry, otherwise cancel targeting before closing unrelated gumps; offer quit only at the world root. Display the active context. Never let typing invoke combat shortcuts.

The current GameController._Input marks events handled, and legacy gumps are not ordinary Godot Control trees. Adding InputMap entries or calling grab_focus alone will not make inventory, shop, or login controls navigable. Define semantic focus for common gumps, keep a virtual cursor for arbitrary shard gumps, and test event consumption explicitly.

Movement should reuse the existing UO step scheduler: eight directions, walk/run, normal collision and server pacing. Add configurable circular deadzone, direction hysteresis, and release on disconnect, focus loss, or suspension. Do not move the mouse cursor to implement the long-term stick movement model; that conflicts with independent targeting.

## Suggested platform layouts

**Mobile:** retain existing bar and macro row; add configurable role favorites and visible target identity. Use a context sheet for crowded-object selection and item destination/quantity. Keep movement and interaction zones distinguishable. Match the existing responsive concept's 48 dp minimum touch targets, safe areas, left-handed option, and no mandatory drag. These are design targets, not measurements of the current bar. Avoid silently moving emergency actions when context changes.

**Native controller / Deck:** prototype left stick movement; right stick/Deck trackpad pointer; south button interact/confirm; east button cancel/back; bumpers cycle candidates; right trigger explicit attack in world context; left trigger hold a favorites wheel; D-pad four role favorites; Menu opens navigation/settings; View opens map. In gumps, D-pad navigates and triggers scroll. Remap everything, provide toggle alternatives to holds, and never require rear buttons. Validate target-mode bindings separately before shipping.

**Steam Input interim:** publish a tested mouse/keyboard layout with trackpad mouse, clicks, Escape, and player-configured macro keys. This can improve access before native input ships, but does not prove full controller support. Later, named Steam Input actions and context layers can expose the same catalog; ensure native events and emulated inputs do not execute actions twice. [Steam Input action layers](https://partner.steamgames.com/doc/features/steam_controller/action_set_layers)

Deck text entry must open automatically when needed through an appropriate Steamworks keyboard API or a controller-operable built-in solution. Manual Steam+X is a fallback. Valve also expects controller-complete flows and readable interface presentation; verify at the Deck's 1280×800 display. [Valve hardware recommendations](https://partner.steamgames.com/doc/steamhardware/recommendations)

**Browser helper now:** a searchable command guide, favorite-layout editor, and input diagnostic prototype can share catalog IDs without a running web client. A future playable browser client needs secure-context gamepad access, connection/disconnection handling, an initial user interaction prompt where required, and browser/OS mapping validation. Clear held actions on blur/visibility changes. A gamepad API does not solve export or shard connectivity. [MDN Gamepad API](https://developer.mozilla.org/en-US/docs/Web/API/Gamepad_API/Using_the_Gamepad_API)

## Delivery order and acceptance

1. **Baseline:** run Deck with supplied game data through login and a world session, in Desktop and Gaming modes; record screenshots/logs. Recheck Android touch and text. Keep these results separate from source inspection.
2. **Shared catalog and contexts:** wire the existing touch actions through stable IDs; add remapping, favorites, target identity, and consistent cancel. Desktop mouse/keyboard and plugin hotkey behavior must remain intact.
3. **Controller vertical slice:** complete login → character selection → walk → open door → open backpack → move a stack → use a healing action → target/cancel → chat → logout without physical keyboard or mouse. Include an arbitrary server gump and purchase confirmation.
4. **Role helpers:** caster, melee/healer, tamer, and gatherer presets; filtered targeting; destination-based inventory transfer. Check spell/item failures and shard restrictions in the journal.
5. **Platform polish:** automatic Deck keyboard, Steam layout/glyphs, mobile reachability and orientation, input hot-swap, suspend/reconnect. Browser guide/prototype can proceed separately; playable web work remains gated on architecture/export.

Test matrix: Android touch and Bluetooth controller; Windows USB/Bluetooth controller; Deck built-ins and external controller in both modes; future browser prototype on Chromium, Firefox, and Safari where available. Include drift, disconnect while moving, text focus, virtual-keyboard overlap, double activation, ground targeting, stacked containers, and left-handed layouts. Record device, OS, engine, shard, input mode, and whether each result is observed or inferred.

To validate “most common,” recruit players across combat, taming, crafting/gathering, and social/trading sessions. With opt-in local instrumentation, count catalog IDs, time to complete, mis-target/cancel rate, menu depth, and fallback to mouse/keyboard. Do not record speech, credentials, or private chat. Rank actions within each role as well as overall; compare touch/controller completion with the mouse baseline before changing defaults.

This research changes documentation only. No runtime support, device certification, or hardware test results are added by this document.
