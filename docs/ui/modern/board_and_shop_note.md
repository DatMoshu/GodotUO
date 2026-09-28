# Bulletin board and shop, Modern: what each would cost (note for the owner)

Gump index rows 8 (BulletinBoardGump) and 7 (ShopGump). Both are **Medium**
risk: usable today with pinch, but dense. They are the last two Modern
candidates, and unlike the others the **shard's packets** drive them, not
the player. This note sets out the port deviation each would need, so you
can pick one or both, or leave them as they are.

Every Modern view so far hooks the classic gump when it is added
(`ModernGumps`, the UIManager.Add hook) and reads the game's own public
state. These two keep their data inside the classic gump instead.

## Bulletin board (row 8): cheap, no edit to a ported file

**How the packets drive it (0x71):** "open" adds a `BulletinBoardGump`.
Then each "summary" finds that gump with `UIManager.GetGump<BulletinBoardGump>`
and calls `AddBulletinObject(serial, text)`. A "message" adds a
`BulletinBoardItem` for that one post.

**The Modern view:** a reader, as the journal has (ADR-0024: "a reader, not
a replacement"). The classic board stays added, because the summaries must
find it, but it is hidden under the Modern view. The view lists the posts
by walking the board's public control tree: each `BulletinBoardObject`
exposes its serial (LocalSerial) and a Label whose Text is public. A tap
on a post sends `Send_BulletinBoardRequestMessage`. Post, reply and remove
are the public `Send_BulletinBoard*` packets the classic buttons send.

**Deviation cost:**

- **Zero lines in ported files.** One new file, `ModernBulletinBoard.cs`,
  of about 250 lines.
- The view must keep the hidden classic board alive while it is open, and
  close it when the view closes.
- **The post itself** (`BulletinBoardItem`) holds its text in private
  fields. Two ways to read it:
  - leave the post as the classic scroll, fitted, which it already is;
  - read the text by reflection, fail safe as the abilities book reads its
    weapons.
  I recommend leaving the post Classic: it is one scrolling text, and a
  thumb can already read it.
- **Risk:** low. If upstream changes the board's controls, the list comes
  up empty and Classic view still works.
- **Estimate:** one evening, with a probe check. The probe needs a board
  on the dev shard (`[add BulletinBoard`; ModernUO has the item).

## Shop, buy and sell (row 7): the dearest, and it needs a decision

**How the packets drive it:** 0x74 (the buy list) and 0x9E (the sell list)
fetch or create a `ShopGump`, add it, then call `gump.AddItem(...)` once per
item, with the **price, name and amount straight from the packet**. Those
live only in the gump's private `_shopItems`. Accepting sends
`Send_BuyRequest` or `Send_SellRequest` with the vendor serial and a list
of (item serial, amount) pairs.

**The Modern view:** a finger-sized list (icon, name, price, stock), a
stepper per row, and a cart total above Buy or Sell. It sends the same
request the classic Accept sends (the same five lines).

It needs the prices, and there are three ways to get them:

| Option | Deviation | Cost | Risk |
|---|---|---|---|
| **A. An accessor in ShopGump** | PORT DEVIATION (GUO) in a verbatim file: a read-only `Items` list (serial, graphic, hue, name, price, amount), about 10 lines | Small, but it is an edit to a ported file, to be carried on every upstream merge | Low. The compiler flags it if upstream reshapes the class |
| **B. Reflection on `_shopItems`** | None in ported files; fail safe as the abilities book is (no prices found: open Classic) | Small | Medium. A private field of a private nested class (`ShopItem`); an upstream rename silently drops back to Classic |
| **C. Leave it Classic** | None | None | It stays pinch-scalable, as it is today |

Things either A or B must also handle, with no further deviation:

- the hook hands the view the classic instance **before** the packet fills
  it. The packet keeps calling `AddItem` on its local reference, so the
  view reads the items a frame later, not when it opens;
- a restock or a second vendor disposes and re-creates the gump. The view
  must follow the new one;
- names can arrive late (`SetNameTo` from 0xD6, the tooltip names). The view
  refreshes its names each tick, as ModernSkills does.

**Estimate:** one to two evenings for A or B, with a probe check. The probe
needs a vendor on the dev shard (`[add` one, such as a Provisioner).

## Recommendation

1. **Bulletin board: go.** It needs no edit to a ported file. The post
   itself stays Classic.
2. **Shop: A if you accept one small accessor in ShopGump**, marked PORT
   DEVIATION (GUO). It is the honest version of B, and it cannot break
   silently. **C** if parity of the ported files outranks the shop on a
   phone for now; pinch already makes it usable.

Neither touches the desktop. Both stay Classic there, as every Modern view
does.
