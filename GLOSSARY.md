# Glossary

Domain terms for the settlement placement advisor. Architecture vocabulary (module, interface,
depth, seam, adapter, leverage, locality) lives in the codebase-design skill; this file names
the domain and the modules at its seams.

## Domain

- **Placement mode**: the settlement action a session is running (`ArmyActionType`):
  `CreateCamp` for settlement placement, `FoundCity` and `FoundCityWithSettler` for city
  founding. All three share `CampPlacementEvaluation`, so the advisor's takeover sees city
  sessions too. Contract: the mode-policy table wires `CreateCamp` and `FoundCity`; a wired mode
  runs the advisor's pipeline with vanilla's full settlement evaluation skipped, writing only
  into its own evaluation, and any other mode is left entirely to vanilla with an INACTIVE
  publish. Every activation releases the tail a previous mode's session left on the shared
  evaluation (see Tail).
- **Takeover point**: `SettlementPlacementCursorSnapshot.EvaluatePlacement`, the snapshot sync
  that stamps the placement mode on the evaluation and asks vanilla's full settlement
  evaluation. For a wired mode the advisor answers there: it stamps the mode, reserves the
  activation baseline (vanilla's `Initialize`), runs the scan pass, and vanilla's full
  evaluation is never called from the patched snapshot.
- **Mode-policy table** (`ModePolicy`): the mode's row in one place: whether the pipeline runs
  for the mode at all (wired), the territory filter that mode evaluates, the district definition
  it scores with, and its tile and army gates. A mode the table has not wired fails closed: no
  rules run, the advisor publishes INACTIVE, and vanilla stays in charge of that mode's evaluation.
- **Scan pass**: the advisor's full evaluation of the placement neighborhood for a cursor
  position, run at the takeover point and cache-served per territory: a refresh re-serves cached
  territories and evaluates only what a moved frame changed.
- **Refresh**: one pass of the placement snapshot's per-cursor update. The advisor hooks both
  halves: the `EvaluatePlacement` takeover for the scan pass, and `UpdateCurrentPosition` as the
  per-refresh takeover for the cursor slot and the publish. In a mode the advisor has taken over it
  owns the refresh: vanilla's whole hover body, its append-one-tile rule and resize-by-10
  included, is skipped, and the cursor slot's gate result and cost trio come from the advisor's
  gate call with no mask. A mode it has not wired, and a surface that cannot answer, leave vanilla
  running its own refresh - its own cursor entry included - with the advisor publishing INACTIVE.
- **Hovered territory**: the territory owning the tile under the placement cursor. Level 1 of the
  placement neighborhood.
- **Pick**: one recommended tile: tile index, weighted FIMS score, and the evaluated
  placement data the FIMS pins read.
- **Cursor context**: the pick computation's full input for one refresh: hovered
  territory, placing army and empire, and the advised counts (clamping lives inside the
  pick computation, minimum 1). Context in, picks out: a null return means the game
  facts were unavailable and the advisor deactivates.
- **Pick set**: the published, immutable result of one pick computation: version, tiles
  (ascending, unique), tile membership, and the entering/leaving diff against the previous
  pick set. The only channel from the simulation thread to the presentation thread.
- **Publish**: swap a new pick set into the volatile channel. Only a pick-content change
  publishes; identical content bumps no version. An INACTIVE publish is the advisor's
  deactivation: in a mode the advisor owns it displays nothing - stars stop and no pin shows,
  and no vanilla surface takes the advice's place. It is the only thing the advisor does on a
  zero-pick result. On a placement mode it does not own, the mode keeps its vanilla surface,
  pin gate included.
- **Tail**: the fixed slot window in `CampPlacementEvaluation.ValidTiles` owned by the
  evaluation writer. The base slot is the cursor slot, reserved at the activation baseline the
  takeover runs vanilla's `Initialize` at; slots above it hold picks, rewritten in place at
  invariant indices every refresh. Vanilla keeps running the refresh, and its hover append lands
  above the reserved window; the writer keeps every slot inside the window outside the current
  admission set scrubbed to -1, and never moves a vanilla entry. Because vanilla binds pins to
  array indices, stable slots keep every retained pick bound; only the cursor tile's element
  changes within one pick set. Vanilla scores the array's whole length, not the valid count, so a
  tail left by a mode switch must be scrubbed, not just counted down.
- **Placement neighborhood**: the set of territories the advisor scans: the hovered
  territory (level 1) plus `RangeLevels` hops over all adjacency edges, covering every
  tile of every territory in it. Sea, cliff, mountain and closed edges each count as a
  hop; there is no movement, zone-of-control, reachability, or land-only filter.
- **Claimable territory**: a territory of the placement neighborhood that passes the
  active mode's territory filter, so that mode's advisor scans it and admits from it. The camp
  row drops `IsEdgeOfTheWorld` and drops a territory that is claimed or already hosts a
  settlement; the FoundCity row drops `IsEdgeOfTheWorld` and drops only a territory that already
  hosts a settlement, so a claimed territory with no settlement on it stays claimable for a city.
  No row claims nothing. No row filters on the territory ocean flag, the claimability-adjacency
  bits, the demilitarized zone, or close borders.
- **Tile pre-filter**: the per-tile checks the mode runs on every tile of a claimable
  territory before asking the gate: water (per tile, never per territory), explored with the
  gate's any-bit visibility predicate, and terrain plus point-of-interest settlement capability
  read with the mode's district definition.
- **Tile gate**: the mode's own placement validity call per tile (`ArmyActionHelper`'s
  `FillCreateCampFailures` tile overload for camp, `FillFoundCityFailures` position overload for
  city founding), asked with the advisor's membership mask. The gate's costs are read and never
  used for membership.
- **Membership mask**: the gate mask `{NotEnoughMoney, NotEnoughInfluence}`: money and
  influence failures are dropped, so an unaffordable tile stays admissible, and every other
  gate failure drops the tile. The mask is the only difference from vanilla's placement gate;
  there is no affordability pre-filter.
- **Army-level gate**: the mode's army-side gate (`FillCreateCampFailures` for camp,
  `FillFoundCityFailures` for city founding), asked once per scan pass before any tile. Any hit
  skips the scan entirely and publishes INACTIVE.
- **Admission set**: one territory's recommended tiles: rank the surviving scored tiles by
  score descending, tile index ascending, then take the territory's quota plus the full top tie
  set. No FIMS floor, no territory-border rule, no distance tie-break, no tie cap, no star cap.
- **Top tie set**: every tile tied with the last admitted score of a territory's
  admission. Admission is the territory's quota (`TopHovered` on the hovered territory,
  `TopNeighbour` on every other) plus the full top tie set: no tie cap, no star cap.
- **Shown set**: the published pick set, equal to the merged admission set of a scan
  pass. Stars and pins are its renderings; nothing outside the shown set displays. The pin
  gate answers membership plus vanilla's hovered rule (the cursor slot's gate result admits
  pins unless it reports a failure other than `NotEnoughInfluence`), and vanilla's generic
  pin filters - tile index, exploration, zero gains and losses, on-screen culling - stay
  unoverridden, so a zero-yield or off-screen member shows its star and no pin.
- **Cursor slot**: the advisor-owned `TailStart` entry of the tail, which the advisor writes
  itself every refresh of a taken-over mode from the mode gate call with no mask: the hovered
  tile's scored entry with no glyph, the failure flags, and the three instant costs, with
  `CurrentPositionIndex` pointing at it. The single surviving path to the ghost, the territory
  highlight, the settlement mouse marker and the hovered-pin rule; the no-entry shape scrubs it
  and the index points at nothing.

## Modules

- **Pick computation** (`PickComputation`): the rules, in SPEC order: army-level gate, the
  mode's territory filter, tile pre-filter, tile gate with the membership mask, scoring on the
  game's own settlement weight table with the resource-tile exclusion kept, then admission.
  Cursor context + game surface in, ordered unique picks out (null = game facts unavailable,
  empty = nothing survived). Scans the whole placement neighborhood. Owns the per-territory cache -
  keyed by territory, quota, empire, placement mode, every frame it reads (the world claim frame
  included), and the territory's own claim stamp, so a claim, unclaim, owner steal, exploration or
  patrol change recomputes only the territory it changed (frames are read from the game surface at
  compute time, never threaded by callers) - plus count clamping, ranking, and admission. No side
  effects, no writes, no budget, no cap, no truncation.
- **Game surface** (`GameSurface`): the seam over the game's internals: tile
  settlement capability, per-tile water, exploration, per-tile placement evaluation, frame
  counters, army lookup, and the territory graph (territory of a tile, claimed flag,
  settlement-site tiles, and the placement-neighborhood relation: every adjacency edge, sea, cliff,
  mountain and closed-border included, with no transition filter). The adapter (`GameApi`) satisfies
  it in the game: reflection for the simulation internals, the public AI snapshot for the territory
  graph. Water is a per-tile fact: sea-flagged territories can host valid camp sites, so the advisor
  never filters by the territory ocean flag. Territory facts fail closed: an unknown territory never
  advises, and an unknown edge set degrades the scan to the territories it has reached. The gates
  come from the same `ArmyActionHelper` calls the game's own placement actions use, per mode, and the
  startup probe checks every member by name.
- **Evaluation writer** (`EvaluationWriter`): puts picks into the evaluation of the mode the session
  is running, and only into that evaluation: the mutating entry points guard on `SettleType`, so a
  mode the mode-policy table has not wired writes nothing. One fixed window per evaluation instance -
  the cursor snapshot's and the range overlay's are live at once - reserved at its activation baseline
  (vanilla's `Initialize`, the tail-reservation point, shared by every placement mode): TailStart is
  the cursor slot, and the slots above it are the pick slots, rewritten in place. Each activation
  releases the window the previous session left, in every mode. Each refresh writes the cursor slot
  from the advisor's gate call with no mask - the failure flags and the three instant costs with it -
  the pick entries `Showable` so the glyph set equals the shown set, leaves vanilla's own glyph flags
  alone, zeroes every slot the refresh released and every slot above the count, then barriers and
  raises `ValidTileCount` and points `CurrentPositionIndex` at the cursor slot.
- **Star renderer** (`StarRenderer`): the advisor's own recommended-tile stars. One entry
  point, takes a pick set; owns the rendered version, the fx handles per tile, and the
  lazy snapshot subscription. Animates entering and leaving tiles only.
- **Pick canvas** (`PickCanvas`): the presentation-side effect interface the star renderer
  drives (start fx on a tile, stop fx). The Unity feedback controller adapter satisfies it.

## Thread contract

- Simulation thread: pick computation, evaluation writer, publish. No Unity object access.
- Presentation thread: star renderer, pick canvas adapter, FIMS pin gate.
- Crossing: one volatile reference swap to an immutable pick set. Presentation readers
  capture the set once per pass and never query the live channel twice in one pass.
- The evaluation array is shared by reference, not copied. The writer's publish order (write
  slots, barrier, raise count) keeps the ghost, the settle action and the pins coherent. The
  only index it repoints is `CurrentPositionIndex`, on the refresh that writes the cursor slot:
  at the cursor slot when the advisor answered the hovered tile, at nothing when it did not. The
  best-placement cascade is retired: the arrays stay unpopulated and no advisor code reads them.
