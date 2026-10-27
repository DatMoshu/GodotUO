# Moving mobile under a roof ? Magincia regression

L0 rendering correction, no option or new renderer. Source baseline e6071aa0.

The companion `.cs.fixture` invokes the actual private scene predicate in a temporary diagnostic build after login. It is deliberately outside the exported client. Copy it as C# into a disposable source stage, invoke `GUO.Host.RoofVisibilityCases.CheckRoofCases(outputDirectory)` once a live GameScene has gathered the world, and inspect every `pass` in `roof-cases.json`. No gameplay input or world packets are sent by these checks; the synthetic mobile is never registered in World.Mobiles or the map.

Prerequisites: original map 1 Magincia geometry loaded around (3481,2532,38), scene max draw Z 127, player outside the building (observed player 3471,2541,36). Do not interpret a pass/fail on a different shard geometry or max-Z as this regression. A failure to load the named fixture is not an accepted result. This is a functional check, never a benchmark.

The old implementation fails exactly the begun-step and restarted-step expectations. The candidate must pass all 12 cases. The same mobile instance is reused intentionally to exercise cache invalidation when a step starts, cancels, restarts, completes and reverses. Negative controls retain idle, merely queued, vertical, nonadjacent, open-courtyard and player behavior.

Visual witness: the merchant at (3481,2532,38) stepping to (3480,2533,38), screen offset (-21,0,0), appears over the tan roof in the baseline. Record the moving sprite and the next idle frame, with player/viewport fixed. Check adjacent outdoor mobiles, visible doors and movement into/out of the room. This fixture does not establish all animation silhouettes, stairs, roof types, maps or picking parity.

The correction extends the existing conservative overhead test to the destination of an already-started adjacent same-height step, ORed with its source result. It does not change CalculateDepthZ, native submission order, doors, networking, the player exemption or roof flags. It is a documented GUO deviation; it is not proof that CUO uses this extra visibility rule.
