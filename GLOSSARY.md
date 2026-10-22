# Glossary

Domain terms for the settlement placement advisor. Architecture vocabulary (module, interface,
depth, seam, adapter, leverage, locality) lives in the codebase-design skill; this file
names the domain and the modules at its seams.

## Domain

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
- **Publish**: swap a new pick set into the volatile channel. Only on pick-content change;
  identical content publishes nothing and bumps no version.
- **Tail**: the FIXED slot window in `CampPlacementEvaluation.ValidTiles` owned by the
  evaluation writer, reserved at activation. Base slot = the cursor slot: vanilla's own
  append point after a full-eval `Initialize` (`UpdateCurrentPosition` appends the cursor
  entry exactly at the baseline count); slots above it hold the picks, rewritten in place
  at invariant indices every refresh. Vanilla entries are never moved, vanilla's appends
  land above the tail. Vanilla's pin presentation binds pins to array indices, so stable
  slot indices keep every retained pick bound: the cursor tile's element is the only one
  that changes between refreshes inside one pick set.

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
  evaluation. Tail protocol: write the pick slots in place, barrier, raise
  `ValidTileCount`; own `CurrentPositionIndex` when the cursor tile is one of the picks.
  Reserves tail capacity in the activation patch; suppresses vanilla placement glyphs.
- **Star renderer** (`StarRenderer`): the advisor's own recommended-tile stars. One entry
  point, takes a pick set; owns the rendered version, the fx handles per tile, and the
  lazy snapshot subscription. Animates entering and leaving tiles only.
- **Pick canvas** (`PickCanvas`): the presentation-side effect interface the star renderer
  drives (start fx on a tile, stop fx). The Unity feedback controller adapter satisfies it.

## Thread contract

- Simulation thread: pick computation, evaluation writer, publish. No Unity object access.
- Presentation thread: star renderer, pick canvas adapter, FIMS pin gate.
- Crossing: one volatile reference swap to an immutable pick set. Presentation readers
  capture the pick set once per pass and answer every membership question from that
  capture; a reader never queries the live channel twice in one pass.
- The evaluation array is shared by reference with the presentation layer, not copied. The
  writer's count publish (write slots, barrier, raise count) is the ordering rule, and
  because the writer never moves an entry, the ghost, the settle action, the
  best-placement cascade and the pins stay coherent; the one index the writer repoints is
  `CurrentPositionIndex` when the cursor tile is a pick.
