# Glossary

Domain terms for the settlement placement advisor. Architecture vocabulary (module, interface,
depth, seam, adapter, leverage, locality) lives in the codebase-design skill; this file names
the domain and the modules at its seams.

## Domain

- **Placement mode**: the settlement action a session is running (`ArmyActionType`):
  `CreateCamp` for camp/outpost placement, `FoundCity` and `FoundCityWithSettler` for city
  founding. All three share `CampPlacementEvaluation`, so the advisor's patch sees city
  sessions too. Contract: camp placement is the only mode it writes; every other mode is a
  strict no-op that hands its pin gate back to vanilla. A camp tail left on the shared
  evaluation is released on the next city activation (see Tail).
- **Refresh**: one pass of the placement snapshot sync: vanilla full eval (only at
  activation / district change) and `UpdateCurrentPosition` (every mouse move). The advisor
  hooks `UpdateCurrentPosition`, so a refresh means "the cursor position was processed".
- **Hovered territory**: the territory owning the tile under the placement cursor. Level 1
  of the range.
- **Range territories**: territories reachable from the hovered territory in the
  adjacency range (`RangeLevels`, center counts as 1), stepping over LAND edges only:
  the game's territory adjacency array also carries sea, cliff, mountain and closed
  edges, and those never count as a range step. The hovered territory is never a range
  territory.
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
  deactivation: stars stop and the FIMS pin gate returns to vanilla. It is the only thing
  the advisor does on a placement mode it does not own.
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
  ordered unique picks out (null = game facts unavailable). Owns the per-territory frame
  cache (frames read from the game surface at compute time, never threaded by callers),
  the evaluation budget, count clamping, ranking, and truncation. No side effects.
- **Game surface** (`GameSurface`): the seam over the game's internals: tile
  campability, exploration, per-tile placement evaluation, frame counters, army lookup,
  and the territory graph (territory of a tile, ocean/claimed flags, camp-site tiles,
  land adjacencies). The adapter (`GameApi`) satisfies it in the game: reflection for the
  simulation internals, the public AI snapshot for the territory graph (its land filter
  keeps the `Open` transition edges). Territory facts fail closed: an unknown territory
  never advises.
- **Evaluation writer** (`EvaluationWriter`): puts picks into the game's placement
  evaluation. Writes pick slots in place, barriers, then raises `ValidTileCount`; owns
  `CurrentPositionIndex` when the cursor tile is a pick. Reserves tail capacity at
  activation and suppresses vanilla glyphs. Camp placement writes only: the mutating entry
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
