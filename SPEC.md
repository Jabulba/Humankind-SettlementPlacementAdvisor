# Settlement Placement Evaluation Rewrite Spec

Planning deliverable of the wayfinder map [Wayfinder map: CampPlacementAdvisor evaluation
rewrite](https://github.com/Jabulba/Humankind-SettlementPlacementAdvisor/issues/1). Every
row here is a decision already made on the map's tickets; this file assembles them for the
implement effort. Evidence lives in the research docs on the pushed (never merged) branches
`research/camp-validity-gate`, `research/territory-claimability`, `research/vanilla-eval-flow`
and the prototype branches `prototype/scan-rules`, `prototype/scan-cost`. `file.cs:line`
anchors point into the decompiled trees under `reference/`.

Scope: the spec only. Implementation (code, harnesses, release) is a separate effort.

## 1. Takeover point and patch surface

The single takeover point is the mode switch
`Interop/SettlementPlacementCursorSnapshot.cs:36` `EvaluatePlacement` (flow ticket
candidate 2). The advisor replaces vanilla's full evaluation
(`SimulationEvaluator.cs:2328` `EvaluateSettlementPlacement`) with its own pipeline,
dispatched on the stamped `SettleType` through a mode-policy table (section 3). Unmapped
types fail closed: no writes, INACTIVE publish, vanilla's error-log path.

The existing `UpdateCurrentPosition` postfix (`Patches.cs:16-19`) becomes the per-refresh advisor
takeover: in a wired mode it writes the cursor slot and publishes the admission set, and vanilla's
refresh body is skipped (section 5); the scan pass runs at the `EvaluatePlacement` takeover.

Disposition of the 16 takeover candidates (flow doc section 8):

| # | Vanilla member | Disposition |
|---|---|---|
| 1 | `SimulationEvaluator.EvaluateSettlementPlacement` (2328) | Replaced by the advisor pipeline; never called from the patched snapshot. |
| 2 | `SettlementPlacementCursorSnapshot.EvaluatePlacement` (36) | Patch point: mode dispatch + scan pass (this section). |
| 3 | `ConstructibleHelper.FillValidSettlePositions` (2478), incl. the 2-turn `FindReachable` flood fill (2480) | Not invoked: the placement-neighborhood scan replaces the candidate source. |
| 4 | `CampPlacementEvaluation.UpdateCurrentPosition` (48) | Refresh takeover (prefix): in a wired mode the advisor writes the cursor slot and publishes, and vanilla's hover append rule (69-82, resize-by-10) dies with its body; a mode not wired keeps vanilla's refresh, its own cursor entry included (section 5). |
| 5 | `CampPlacementEvaluation.Initialize` (34) + `DistrictPlacementEvaluation.Initialize` (130) | Kept as the tail-reservation point in every mode (`Patches.cs:40-56`). |
| 6 | `CampPlacementEvaluation.ReplaceBestCampPlacementAtIndex` (85) + best-of-2 loop (`SimulationEvaluator.cs:2389-2412`) | Deleted in all modes (the only `Showable` writer; unbounded tie sets need it gone). |
| 7 | `SimulationEvaluator.EvaluateOneSettlementPlacement` (2419) | Kept as the scoring primitive, called through `GameApi`. |
| 8 | `InitSettlementScorePonderation` (3649) + `settlementScorePonderation` (241) | Kept: the vanilla weight table the scoring reads. |
| 9 | `IsTileAtTerritoryBorder` (3662) + `MinimalFimsForCampPlacement` 25 floor (2399) | Dropped: never consulted. |
| 10 | `ArmyActionHelper.FillCreateCampFailures` tile overload (465) and the mode siblings | Called via reflection for suggestions; execution validators (`DepartmentOfTheInterior.cs:4892`, `DepartmentOfTransportation.cs:1503`) stay fully vanilla. |
| 11 | `SettlementPlacementPinsSubset.PinMustMeShown` (25) | Patched: gate = pick-set membership + vanilla's hovered rule (section 5). |
| 12 | `SettlementPlacementPinsSubset.RefreshInfos` (46) | Unchanged; index-bound pin coherence is met by slot stability (section 6). |
| 13 | `SettlementPlacementCursor.cs:172` `DataChanged` (195-196 `onlyShowShowable: true`) | Unchanged; advisor-owned `Showable` makes stars follow for free. |
| 14 | `MouseMarkersWindow.cs:341` `CampCreationClient.Refresh` (344-347) | Unchanged; the cursor slot keeps writing `SettleFailure` and the cost trio. |
| 15 | `PlacementPin.cs:201` build-time `SettleType` branch | Keep `SettleType`/`BuildTime` coherent; the label stays camp-only. |
| 16 | `SettlementPlacementCursor.cs:125` `DeactivateCursor` (127-134) | Patch kept: mode exit publishes INACTIVE. |

Vanilla's `Synchronize` trigger set (`ConstructiblePlacementCursorSnapshot.cs:69`) is not
changed; the pipeline is evaluated against it.

Reference-only facts: no AI caller of the evaluation exists (AI reads
`SettleCostByTerritory`, `AI/MajorEmpire.cs:166`); the scenario editor bypasses the flow
(`EditorSettlementPlacementCursor.cs:19-27`). Both are out of scope.

## 2. The pipeline (simulation thread)

Steps per scan pass, in order:

1. **Mode dispatch** on the stamped `SettleType` through the mode-policy table (section 3).
   Unmapped types fail closed (INACTIVE).
2. **Army-level gate**, once per refresh, via the mode's action gate with
   `computeMinimumCost: false`. Any hit publishes INACTIVE and skips the scan. Camp rows:
   decoy `ActionProhibited` (`ArmyActionHelper.cs:365-370`), `ArmyLockedByBattle` (371-374),
   `ArmyLocked` (375-378), `IsLesserEmpire` (391-395), `IsRented` (396-400), `NoLandUnit`
   (401-406), `AtLeastOneSeafaringUnit` (407-412), `DiplomatProhibitedAction` (413-417),
   `TrespassingStatus` (418-421), `MinorEmpireAlreadyHaveSettlement` (422-425), nomad
   `NotEnoughUnits` (426-434). The row-12 cost trio (435-457) is never consulted.
3. **Scan pass** over the placement neighborhood: the hovered territory (level 1) plus
   `RangeLevels` hops over **all** adjacency edges
   (`Snapshots.World.Territories[t].Adjacencies[].TerritoryIndex`; no `TerritoryTransition`
   filter; transitions are an AI-layer mobility overlay), every tile of every territory in
   it. No movement points, no zone of control, no reachability. No cap, no budget, no
   truncation. Unknown adjacency degrades to hovered-only plus a `GameApi.Fail` log.
4. **Territory filter** (claimable territory), per mode (section 3), evaluated before any
   tile work. Never a filter: territory `IsOcean`, `AnyClaimableTile` (map-load stale),
   `DemilitarizedByEmpireIndex`, close-border bits.
5. **Tile pre-filter**, cheap snapshot reads before the gate: water (per tile; sea-flagged
   territories host valid camp sites), explored with the gate's any-bit predicate
   `((ExplorationMap | BattleExplorationMap) & bits) != 0` (`VisibilityController.cs:255-258`)
   in all three modes, terrain + POI via `ConstructibleHelper.CheckTerrainAndPOI` 2-arg
   (2824-2828) with the mode's district definition.
6. **Tile gate**: one reflection call per pre-filter-surviving tile to the mode's tile gate
   (section 3). Membership: `flags == None` minus the mask
   {`Part1.NotEnoughMoney`, `Part1.NotEnoughInfluence`}. No affordability prefilter: the
   gate call runs on every surviving tile, so unaffordable tiles stay in the recommendation
   set and the hover path keeps vanilla's `SettleFailure` and instant costs.
7. **Scoring** via `EvaluateOneSettlementPlacement` on the vanilla weight table: Food 1.5,
   Industry 1, rest 0 (`SimulationEvaluator.cs:3649-3660`); the resource-tile exclusion
   (2399) stays. Dropped: the 25 floor, `IsTileAtTerritoryBorder`, the distance-to-army
   tie-break (2404-2411).
8. **Admission** per territory: sort by weighted score descending, tile index ascending;
   admit the clamped quota (`TopHovered` on the hovered territory, `TopNeighbour` on every
   other), then admit every tile tied with the last admitted score (the top tie set). No
   tie cap, no star cap. Example: quota 2 with 3 tiles tied at rank 1 publishes 3; quota 2
   with 1 tile at rank 1 and 3 tiles tied at rank 2 publishes 4.
9. **Publish**: the merged admission set, ascending unique by tile index. Zero picks
   publishes INACTIVE (displays nothing; no vanilla fallback in any mode).

**Cadence and cache.** Compute only on refresh, served by the per-territory cache. Key:
territory index + district frame + construction frame + visibility frame + empire index +
admitted count + placement mode + `World.TerritoryInfo.Frame` (bumped by claims, unclaims,
owner steals, exploration, patrol changes at `World.cs:870,2358,2383,2399,2780,2820`;
bumps between refreshes cost nothing). Per mouse move: cache hits plus at most one newly
entered territory. Worst case is one full pass per refresh; no coalescing machinery.

**Advice vs legality.** Execution validators stay fully vanilla
(`DepartmentOfTheInterior`/`DepartmentOfTransportation` gates). The advisor makes no
legality promise: an advised tile can be rejected at order time (army overload, costs,
foreign army on arrival, state drift). Advice is a recommendation, never a guarantee.

## 3. Mode-policy table

| Mode | Territory filter (drop) | Tile gate (suggestion) | Army-level gate | District definition |
|---|---|---|---|---|
| `CreateCamp` | `IsEdgeOfTheWorld` (510-514); `Claimed \|\| SettlementGUID != 0` (515-518) | `FillCreateCampFailures` tile overload (465-545) | army overload, rows 365-434 | `CampDefinition` |
| `FoundCity` | `IsEdgeOfTheWorld`; `SettlementGUID != 0` (649-652) | FoundCity gate (629-672) | `FillFoundCityFailures` | `CityCenterDefinition` |
| `FoundCityWithSettler` | `IsEdgeOfTheWorld`; `SettlementGUID != 0` except own-empire camp/pre-camp settlement (740-753, replaceability 763-771) | settler gate (713-777) | `FillSettlerAbilityFailures`; no settler unit means INACTIVE | `CityCenterDefinition` |

All gate calls use the section-2 membership mask and the explored any-bit requirement (the
FoundCity gate returns clean on unexplored positions at 638-641, so explored is the
advisor's rule, not the gate's). Vanilla's camp-gate-for-all
(`ConstructibleHelper.cs:2488`) is gone. The `startingPack` parameter stays out of scoring
(faithful: the vanilla evaluator never reads it).

## 4. Writes in all three modes

The city no-op contract is retired. New invariant: writes happen only through the active
mode's advisor pipeline, into that mode's evaluation only. The tail window,
barrier-then-count order, and scrub rules survive unchanged in every mode. The three mode
guards (`Advisor.cs:32`, `EvaluationWriter.cs:36`, `EvaluationWriter.cs:73`) become mode
dispatch.

## 5. Presentation contract (shown set)

The shown set is the published pick set (the admission set); stars and pins are its two
renderings of one surface.

1. Every member gets a star in all three modes: no star cap, no tie cap. Ties are accepted
   as rare.
2. **Pin gate = membership in the published pick set, plus vanilla's hovered-tile rule;
   non-members off.** The gate reads `PickSet.Current` membership (no quota/tie
   distinction reaches presentation; `PickSet` gains no fields). Vanilla's hovered rule
   stays (true unless `SettleFailure != None && != NotEnoughInfluence`).
3. **Vanilla's generic pin filters stay in force, unoverridden**: `TileIndex < 0`,
   unexplored, zero-gains-and-zero-losses rejects plus on-screen culling
   (`PlacementPinsSubset.cs:53-67`). A member with zero FIMS yields gets a star and no pin;
   off-screen members get stars and no pins.
4. `BestCampPlacementTileIndexes` retires: the array stays all `-1`
   (`CampPlacementEvaluation.cs:37-45`), no consumers, and the mod's suppression loop over
   it (`Patches.cs:96-101`) is deleted.
5. **Cursor slot.** The advisor writes the `TailStart` entry itself every refresh: mode
   gate call with no mask (money/influence included), `SettleFailure` and the three instant
   costs written faithfully, `CurrentPositionIndex` pointed at that slot. This is the single
   surviving path to the ghost, the territory highlight, the camp mouse marker, and the
   hovered-pin rule, in every mode.
6. **`Showable` = pick-set membership, advisor-written.** The writer's glyph-suppression
   loop over vanilla entries (`EvaluationWriter.cs:101-109`) and the mode-switch
   suppressed-glyph leak die. The `Selected` glyph (cursor-tile style,
   `DistrictPlacementTileFeedback.cs:220,250`) stays.
7. **Cost failure surfaces on hover only.** An unaffordable pick looks identical to an
   affordable one (same star, same pin yields). The failure reaches the player through the
   cursor slot: camp mode gets the mouse marker with money/influence/unit costs, city modes
   get the red ghost from `SettleFailure`. No affordability color coding on stars, no
   marker on pins, no pre-hover warning.
8. **One presentation contract in all three modes.** Stars, pins, gate, and cursor slot are
   identical in all modes; mode-specific vanilla surfaces stay mode-only (camp mouse
   marker, camp-only build-time pin label `PlacementPin.cs:195-217`, generic city cursor
   window `SettlementPlacementCursor.cs:117`). City modes get stars and pins for the first
   time.
9. **INACTIVE displays nothing in every mode**: stars cleared, no pins, no cursor-slot
   entry, no glyphs, no vanilla fallback.

## 6. Write-side contract (EvaluationWriter)

1. **Tail capacity is derived at activation from config**: worst-case admission-set size
   for the configured `RangeLevels` (per-territory quota plus tie headroom), replacing the
   constant 128. Grow-on-demand is kept; `ReleaseTail` scrubs the full region ever
   reserved, not the constant window.
2. Write order survives: write pick slots in place at invariant indices, barrier, raise
   `ValidTileCount`. Pins bind to array indices (`PlacementPin.cs:125-128`), so slot
   stability keeps every retained pick bound.
3. **Zero-fill above `ValidTileCount` is the writer's per-refresh duty**: slots above the
   count are zeroed (`Initialize(-1)` semantics); the writer restores the invariant
   incrementally by zeroing slots released since the previous write. No full-array sweep;
   nobody scores the array length (the cascade deletion removed the only consumer,
   `SimulationEvaluator.cs:2389-2391`).
4. Mode switch: any mode's activation releases the previous mode's tail (full-region
   scrub). A settlement tail left after a mode switch is scrubbed, never merely counted
   down.

Array invariants after every refresh (harness acceptance rows):

- `Showable` set == pick-set membership.
- Slots above `ValidTileCount` are zeroed.
- `CurrentPositionIndex` points at the cursor slot.
- Cursor-slot fields (`SettleFailure` + cost trio) are populated by the gate call.
- The pin gate answers membership + hovered rule; vanilla generic filters decide pixels.
- Mode-switch scrub covers the max region ever reserved.
- INACTIVE clears stars and pins in all modes.

## 7. Module mapping

Deep-module vocabulary per the codebase-design skill: the pipeline is one deep module
behind the `EvaluatePlacement` seam; the mode-policy table is an internal seam; `GameSurface`
stays the adapter seam to the game; harnesses cross the same seams as production callers.

- **`Patches.cs`**: add the `EvaluatePlacement` takeover patch (scan pass entry, mode
  dispatch); keep the `Initialize` postfix (tail reservation, all modes), the
  `DeactivateCursor` postfix (INACTIVE on exit), and the `UpdateCurrentPosition` takeover
  (prefix): in a wired mode the advisor writes the cursor slot and publishes, and vanilla's
  refresh body is skipped; a mode it has not wired returns vanilla's own refresh. Re-bind the
  pin-gate postfix to membership + hovered rule; delete the best-placement suppression loop.
- **`Advisor.cs`**: `CreateCamp` guard becomes mode-policy dispatch; army-level gate call
  added; INACTIVE publish semantics stay (unavailable facts, unmapped mode, mode exit,
  zero picks all publish INACTIVE).
- **`PickComputation.cs`**: neighborhood scan over all adjacency edges; territory filter and
  gate from the mode-policy table; cache key gains placement mode +
  `World.TerritoryInfo.Frame`; admission = quota + top tie set. Deleted:
  `MaxEvaluationsPerRefresh` (53-57), `MaxRangeTerritories`, the budget and truncation
  paths (`PickComputation.cs:134-138,197-225`) and the no-cache-of-truncated rule. Kept:
  per-territory cache, ascending unique merge, clamping (minimum 1).
- **`GameSurface.cs` / `GameApi.cs`**: `LandAdjacentTerritories` renames to
  `AdjacentTerritories` (no transition filter; `GameApi.cs:310-324` `Open`-only filter
  dies). Explored binding moves from all-bits (`IsWorldPositionExploredFor`,
  `GameApi.cs:110`) to the gate's any-bit predicate. New surface members: the mode tile
  gates and mode army gates (reflection), the mode district definitions
  (`CampDefinition`, `CityCenterDefinition`), territory facts
  (`IsEdgeOfTheWorld`, `Claimed`, `SettlementGUID`, `EmpireIndex` on snapshot
  `TerritoryInfo`), and the `World.TerritoryInfo.Frame` counter. `Resolve()` checks the new
  reflected member names; territory facts keep failing closed.
- **`EvaluationWriter.cs`**: section 6 (derived capacity, full-region scrub, per-refresh
  zero-fill, cursor-slot ownership, writes in every mode).
- **`PickSet.cs`**: no changes; membership == shown set, one volatile swap, version-gated,
  entering/leaving diffs.
- **`StarRenderer.cs`**: unchanged; it renders the published set (every member, no cap) via
  the entering/leaving diff.
- **`Plugin.cs` / `AdvisorConfig.cs`**: section 8; resolve failure becomes a hard, loud
  failure (game closes, log opens), the silent
  `Disabled, unresolved game members` path dies.

## 8. Config surface

| Knob | Type | Default | Bounds | Semantics |
|---|---|---|---|---|
| `TopHovered` | int | 2 | >= 1, clamped | Per-territory admission quota on the hovered territory: mandatory minimum count plus the full top tie set |
| `TopNeighbour` | int | 1 | >= 1, clamped | Same quota semantics on every other territory in the placement neighborhood; 0 not allowed, no hovered-only display |
| `RangeLevels` | int | 2 | >= 1, no upper bound | Placement-neighborhood hop count over all adjacency types; the hovered territory counts as level 1 |

Nothing else: no score-floor knob (the 25 floor stays dropped outright), no mode toggles,
no tie cap (tie sizing is the presentation contract), no master `Enabled` knob (disabling
is the mod loader's job). Auto-disable on unresolved game members is retired: load failure
is a hard, loud failure (the game closes and the log opens). `RangeLevels` has no upper
clamp (decided by the scan-cost prototype). Help-text rewrites (exact strings):

- `TopHovered`: "Admission quota on the hovered territory: this many tiles minimum, plus
  every tile tied with the last admitted score."
- `TopNeighbour`: "Admission quota on every other territory in the placement neighborhood:
  this many tiles minimum, plus every tile tied with the last admitted score."
- `RangeLevels`: "Placement-neighborhood hops from the hovered territory, the hovered
  territory counts as level 1: 3 = hovered + adjacent + adjacent-to-adjacent. Every
  adjacency edge counts as a hop: sea, cliff, mountain and closed-border edges included."

## 9. Performance acceptance

No runtime cap or budget. Acceptance rule: the redesigned harness's wide-scan case
(`RangeLevels = 3`, ~1000 gate calls, ~4300 tiles) completes within 5 ms per refresh on the
net10 harness, with an explicit 4x inflation allowance for the Unity Mono runtime; no
in-game benchmark at planning.

Measured floor (`prototype/scan-cost`, net10): gate reflection 245 ns light / 433 ns with
settle-cost body per call (reflection overhead ~250 ns over direct); R=2 neighborhoods
13-32 territories / 624-2016 tiles / 155-492 gate calls; R=3 wide 89 territories / 4272
tiles / 1014 gate calls; cold pass 0.1-0.6 ms, cache-hit refresh 0.0-0.2 ms,
claim-triggered recompute = cold. The un-memoized worst case is 0.6 ms cold = ~2.4 ms
Mono-inflated, inside the bound.

## 10. Harness redesign

- `scripts/test-range.sh`: the cap, budget, and truncation cases die with the scan
  decision; the land-adjacency cases become all-edge neighborhood cases. New cases: quota +
  top-tie admission (both worked examples in section 2), cache invalidation on
  `World.TerritoryInfo.Frame` bumps, hovered-only fallback on unknown adjacency,
  per-mode territory filters and gate masks, scoring (weights + resource-tile kept; 25
  floor, border rule, distance tie-break dropped).
- `scripts/test-noop.sh`: the FoundCity/Settler zero-write block retires (the modes
  decision inverts it); the surviving cases become the contract rows of section 6: pin gate
  = membership + hovered rule, cursor-slot fields populated by the gate call, zero-fill
  invariant above the count, `Showable` == membership, full-region scrub on mode switch,
  INACTIVE clears stars and pins in all modes; mode-switch tail release, zero-pick INACTIVE
  (new meaning), sea-flag, and pin-gate cases survive in their new semantics.
- `scripts/scancircuit/` + `scripts/test-scan.sh` graduate from `prototype/scan-cost` into
  the suite with the section-9 acceptance row (cold pass, cache-hit refresh,
  claim-triggered recompute).

Harnesses stay `net10.0` with `LangVersion 7.3`; new production files go into the harness
csproj `<Compile Include>` lists (the harnesses do not glob).

## 11. Vocabulary migration (exact rows)

GLOSSARY:

- Delete the **Range territories** entry.
- Add: **Placement neighborhood** (hovered territory plus `RangeLevels` hops over all
  adjacency edges; every tile of every territory in it). **Claimable territory** (passes
  the active mode's territory filter). **Top tie set** (every tile tied with the last
  admitted score of a territory's admission). **Scan pass** (one full evaluation of the
  placement neighborhood, cache-served per territory). **Shown set** (the published pick
  set = the admission set; stars and pins are its renderings). **Cursor slot** (the
  advisor-owned `TailStart` entry carrying the hovered tile's gate result).
- Rewrite the Placement mode clause: the settlement-placement-only writing and city no-op
  die; all three modes write through their mode's advisor pipeline.
- Rewrite Publish: "hands its pin gate back to vanilla" dies; INACTIVE displays nothing
  everywhere.
- Rewrite Evaluation writer: "Settlement placement writes only" dies; capacity is derived
  from config; scrub covers the full region ever reserved.
- Rewrite Tail: the cursor slot is advisor-owned (vanilla's append dies); pins stay
  index-bound by slot stability.
- Rewrite Refresh: the advisor's pipeline runs on every snapshot-sync refresh; scan work is
  cache-served, mouse moves cost cache hits plus at most one new territory.
- Rewrite Pick computation: budget/truncation wording dies; admission = quota + top tie
  set.
- Rewrite Game surface: "land adjacencies" becomes "adjacencies" (no transition filter);
  explored is the gate's any-bit predicate.
- Hovered territory, Pick, Pick set, Pick computation (minus budget wording), Star
  renderer, Pick canvas keep their text.

README:

- Summary paragraph (8-11): "the hovered territory gets its top two recommendations; every
  other territory within land-adjacency range gets its best one. City founding is left
  completely alone." becomes: the hovered territory gets its admission quota, every other
  territory in the placement neighborhood gets its quota plus every tied tile, and all
  three settlement modes (camp, city, settler) get the same treatment.
- Features: the "Land-aware range" bullet dies (adjacency hops, all edge types); the "Safe
  around city founding" bullet dies (city sessions are advised).
- Default-rules table (63-66): quotas stay 2/1 with tie-set wording; the water paragraph
  (68-72) keeps the per-tile water rule and drops the "in range" wording.
- Configuration section (82-92): the table and help strings take section 8's exact rows;
  the "land-adjacency range" wording dies.
- How-it-works (94-108): steps become the section-2 pipeline (neighborhood scan, territory
  filter, mode gate, admission with ties); the all-writes-restricted-to-`CreateCamp`
  paragraph dies.
- Compatibility (114-116): the auto-disable paragraph dies; the new wording is the hard,
  loud failure (game closes, log opens).
- Troubleshooting (128-134): "Vanilla recommendations take over" row dies (zero picks
  display nothing); the `Disabled, unresolved game members` row dies (replaced by the
  hard-fail wording); "not city founding" dies (city modes are advised).
- Building-from-source (154-157): harness names gain `test-scan.sh`.

## 12. Out of scope (for the implement effort)

- The scenario editor placement flow (`EditorSettlementPlacementCursor` bypasses the
  snapshot/evaluation flow).
- AI-facing behavior (no AI caller exists; the rewrite is player-facing).
- Any runtime cap, budget, affordability prefilter, tie cap, mode toggle, score floor,
  `Enabled` knob, or auto-disable: all decided dead.
