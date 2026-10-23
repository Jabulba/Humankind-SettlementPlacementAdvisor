# Glossary

Domain terms for the settlement placement advisor. Architecture vocabulary (module, interface,
depth, seam, adapter, leverage, locality) lives in the codebase-design skill; this file names
the domain and the modules at its seams.

## Domain

- **Placement mode**: the settlement action a session is running (`ArmyActionType`):
  `CreateCamp` for settlement placement, `FoundCity` and `FoundCityWithSettler` for city
  founding. All three share `CampPlacementEvaluation`, so the advisor's patch sees city
  sessions too. Contract: settlement placement is the only mode it writes; every other mode is a
  strict no-op that hands its pin gate back to vanilla. A settlement tail left on the shared
  evaluation is released on the next city activation (see Tail).
- **Refresh**: one pass of the placement snapshot sync: vanilla full eval (only at
  activation / district change) and `UpdateCurrentPosition` (every mouse move). The advisor
  hooks `UpdateCurrentPosition`, so a refresh means "the cursor position was processed".
- **Hovered territory**: the territory owning the tile under the placement cursor. Center of
  the placement neighborhood (level 1), and the only territory scanned when the game's
  adjacency data is unavailable.
- **Placement neighborhood**: the hovered territory plus the territories reachable from it in
  the adjacency range (`RangeLevels`, center counts as 1), stepping over adjacency edges of
  any type. Adjacency is the game's full adjacency relation; `TerritoryTransition` types
  (coast, shallow/deep water, cliff, mountain, closed) are an AI-layer mobility overlay and
  never filter it.
- **Claimable territory**: a neighborhood territory the placing army may settle under the
  session's placement mode: the territory-level facts (world edge, existing settlement,
  mode-specific settlement ownership) pass, evaluated before any tile work.
- **Scan pass**: one evaluation of the placement neighborhood's tiles on the simulation
  thread: claimability filter, per-tile validity, per-tile scoring, per-territory ranking.
- **Top tie set**: the picks a territory publishes: its admitted count, plus every additional
  tile tied in score with the last admitted tile.
- **Pick**: one recommended tile: tile index, weighted FIMS score, and the evaluated
  placement data the FIMS pins read.
- **Cursor context**: the pick computation's full input for one refresh: hovered
  territory, placing army, empire, placement mode, and the advised counts (clamping lives
  inside the pick computation, minimum 1). Context in, picks out: a null return means the
  game facts were unavailable and the advisor deactivates.
- **Pick set**: the published, immutable result of one pick computation: version, tiles
  (ascending, unique), tile membership, and the entering/leaving diff against the previous
  pick set. The only channel from the simulation thread to the presentation thread.
- **Publish**: swap a new pick set into the volatile channel. Only a pick-content change
  publishes; identical content bumps no version. An INACTIVE publish is the advisor's
  deactivation: stars stop and the FIMS pin gate returns to vanilla. It is the only thing
  the advisor does on a placement mode it does not own, and on a zero-pick result: an ACTIVE
  empty set would suppress vanilla's pins without replacing them, so nothing-to-advise
  publishes inactive.
- **Tail**: the fixed slot window in `CampPlacementEvaluation.ValidTiles` owned by the
  evaluation writer. The base slot is the cursor slot, vanilla's append point after a
  full-eval `Initialize` (`UpdateCurrentPosition` appends the cursor entry at the baseline
  count); slots above it hold picks, rewritten in place at invariant indices every refresh.
  Vanilla entries are never moved and its appends land above the tail. Because vanilla binds
  pins to array indices, stable slots keep every retained pick bound; only the cursor tile's
  element changes within one pick set. Vanilla scores the array's whole length, not the valid
  count, so a tail left by a mode switch must be scrubbed, not just counted down.

## Modules

- **Pick computation** (`PickComputation`): the rules. Cursor context + game surface in,
  ordered unique picks out (null = game facts unavailable). Owns the placement neighborhood
  walk, the per-territory frame cache (frames read from the game surface at compute time,
  never threaded by callers), count clamping, and per-territory ranking with ties. No side
  effects.
- **Game surface** (`GameSurface`): the seam over the game's internals: tile
  settlement capability, per-tile water, exploration, per-tile placement evaluation, frame
  counters, army lookup, and the territory graph (territory of a tile, claimed flag,
  settlement-site tiles, adjacencies). The adapter (`GameApi`) satisfies it in the game:
  reflection for the simulation internals, the public AI snapshot for the territory graph
  (adjacency is taken with no transition filter: transitions are a mobility overlay, not
  adjacency). Water is a per-tile fact: sea-flagged territories can host valid camp sites, so
  the advisor never filters by the territory ocean flag. Territory facts fail closed: an
  unknown territory never advises.
- **Evaluation writer** (`EvaluationWriter`): puts picks into the game's placement
  evaluation. Writes pick slots in place, barriers, then raises `ValidTileCount`; owns
  `CurrentPositionIndex` when the cursor tile is a pick. Reserves tail capacity at
  activation and suppresses vanilla glyphs. Settlement placement writes only: the mutating entry
  point guards on `SettleType`, and city activation only releases the advisor's own slots.
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
  slots, barrier, raise count) keeps the ghost, settle action, best-placement cascade and
  pins coherent. The only index it repoints is `CurrentPositionIndex`, when the cursor tile
  is a pick.
