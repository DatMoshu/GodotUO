# DEBUG diagnostic sparse reference — lead22286

ReceiverReferenceIndex.cs and ReceiverReferenceProbe.cs are enclosed in DEBUG.
They are unwired internal pure C# diagnostics. BigInteger, SortedSet, List and
array allocations are intentional. Never call them in a render callback,
measured gameplay path, export/Release path or native publication adapter.
No new dependency; System.Numerics.BigInteger is supplied by .NET.

V3's seven files remain immutable, separately frozen by patch763342af.
Qualify pure v3 local geometry/weather first when lead grants actual opportunity;
then invoke ReceiverReferenceProbe.Run() in an isolated DEBUG harness. No probe
registration, project file, scene, launchers or runtime settings changed here.
Neither source packet has been compiled/executed by this continuation.

The event walk compares exact rational crossing fractions using allocating
BigInteger coordinates. Closed starts/ends, grid-line pairs and corner-side
cells are retained; source-bound binary32 endpoints are never shortened/snapped.
The independent oracle uses closed unit-box slab intersections and an alternative
binary64 decoding of the exact float input. It compares full sets, not counts.
No custom192/384-bit kernel is implemented.

Sparse physical cells are manually supplied copied ReceiverCover records bound
to one exact frame key. No map access/load or live-object reference exists.
Complete empty, unloaded, missing, incomplete, unsupported, duplicate and budget
failures stay distinct; failure copies zero records and preserves caller output.
Receipts retain candidate coverage/counts separately from Copied; only a true
return admits the result. Same immutable cover records feed the v3 vertical query.

Existing hard caps:330tile horizontal domain,1024cells/events per corridor,
8192globalcells/32768globalcovers,8covers/cell,64native nodes,64queries/65536refs,
8192covers/corridor,16directory probes. Local32x32/4-layer storage is unchanged.
Ordered-tree dedup has no hash probes; its1024cell cap is checked before insertion.
Directory collisions remain capped. These logical caps DO NOT establish a CLR
memory/time budget: debug heap churn is not the proposed3.498MiB production
payload. No shipping/hot-path index or performance claim is made.

The source probe contains axes/diagonal/sign/reversal/boundary/next-float cases,
all nine L.8/1/4xZ-128/0/382 cases with fractional/integer origins, full endpoint
identity, complete-empty positives and missing-cell negatives. Additional source
cases cover unloaded/stale-key/unsupported/incomplete cells, global/cell/query/
probe capacities, checked capacity primitives, immutable copied cover records,
destination preflight and shared vertical shelter. Exact1024/8192/65536 capacity
primitive tests are distinct from actual domain rays; no native gate is bypassed
to reach an otherwise unreachable memory bound.

Remaining limits: manual synthetic residency proves no actual loaded map
coverage; physical producer/topology invalidation, supporting plane/owner/kind,
native footprint admission, callbacks, RD/shader/mask publication and frame cost
are unimplemented/unapproved.64point queries never certify a whole silhouette.
Keep actual native coverage/performance and visual/Gemini gates independent.
No owner interaction, resources, accepted1041/1037 rerun or uploads authorized
by this reference packet. Freeze before any admitted build.
