# Glossary

Domain terms for the settlement placement advisor, and the modules at its seams. Architecture vocabulary lives in the
codebase-design skill.

## Domain

- **Placement mode**: the settlement action a session is running: `CreateCamp`, `FoundCity` or
  `FoundCityWithSettler`.
- **Takeover point**: `SettlementPlacementCursorSnapshot.EvaluatePlacement`, the snapshot sync the advisor answers for a
  wired mode.
- **Mode-policy table** (`ModePolicy`): a mode's row in one place - territory claimability, district definition, tile
  gate and army gates, each gate naming a `BindingShape` the startup probe can bind. A mode is wired when its row
  exists. The single source of mode dispatch.
- **Scan pass**: the advisor's full evaluation of the placement neighborhood for one cursor position.
- **Refresh**: one per-cursor update of the placement snapshot - its scan pass, cursor slot write and publish.
- **Hovered territory**: the territory owning the tile under the placement cursor.
- **Pick**: one recommended tile - tile index, weighted FIMS score, and the evaluated placement data the pins read.
- **Cursor context**: the pick computation's whole input for one refresh: hovered territory, placing army and empire,
  the advised counts, the neighborhood range, and the mode.
- **Pick set**: the immutable result of one pick computation - the published ascending unique tiles the pin gate answers
  membership on.
- **Presentation view** (`PresentationView.Current`): the advisor's only cross-thread channel - one snapshot of a pick
  set, its mode, and the cursor slot's gate answer with the tile it scored.
- **Inactive**: the advisor's off state - a presentation view with no pick set.
- **Publish**: the swap of one presentation view into that channel.
- **Tail**: the entry window the evaluation writer owns in the active mode evaluation's
  `CampPlacementEvaluation.ValidTiles`; the **cursor slot** is its first entry.
- **Placement neighborhood**: the territories a scan covers: the hovered territory, counted as level 1, plus
  `RangeLevels` - 1 hops over every adjacency edge, sea, cliff, mountain and closed-border edges included.
- **Claimable territory**: a neighborhood territory that passes the mode row's territory claimability.
- **Tile pre-filter**: the per-tile checks run on every tile of a claimable territory before the mode's gate is asked:
  water, exploration, terrain and settlement capability.
- **Tile gate**: the mode's own placement validity call for one tile. The game surface answers it unmasked as a **gate
  answer**: failure flags, plus money/influence/unit costs on the camp gate; the city and settler gates report flags
  only.
- **Resource-tile exclusion**: the drop of a gate-passed tile whose point of interest is a resource deposit
  (`IsTileResourceTile`); a lookup failure counts as a resource tile, so the tile is dropped.
- **Membership mask**: the pick computation's mask over the gate answer that drops money and influence failures, so an
  unaffordable tile stays admissible.
- **Army-level gate**: the army-side gates named by the mode's row - vanilla's shared army-state row first, then the
  mode's own - asked once per scan pass.
- **Admission set**: one territory's recommended tiles - its quota plus the full top tie set.
- **Top tie set**: every tile tied with the last admitted score of a territory's admission.
- **Ranked survivors**: one territory's tiles that passed the pre-filter, the membership mask, the resource-tile
  exclusion and the game's own evaluation, sorted score-descending then tile-ascending. The pick computation's cache
  unit and the admission's input; the quota applies only when a role (hovered or neighbour) slices them.
- **Shown set**: the published pick set - the tiles vanilla's overlay and pins show.
- **White hex**: vanilla's own advised-tile overlay - its tile-feedback rendering of the shown tiles, driven by the
  writer's `Showable` entries. The advisor renders nothing itself. _Avoid_: `Recommanded`, the game's own recommendation
  flag.
- **Cursor slot**: the tail's first entry, written on every refresh: the scored entry for the hovered tile, carrying
  vanilla's hovered glyph when the hovered rule admits it - vanilla's kind switch recolors it when the hovered tile
  is also a shown tile.
- **Glyph**: the marker an evaluation entry carries, its `Showable` flag.
- **Hovered rule** (`HoveredRule`): vanilla's rule for the tile under the cursor - the pin and the glyph show unless
  its gate answer reports a failure other than insufficient influence. The tile under the cursor renders on this
  rule, never on shown-set membership.

## Modules

- **Pick computation** (`PickComputation`): the rules in pipeline order, from the army-level gate to admission, with the
  per-territory cache.
- **Game surface** (`GameSurface`): the seam over the game's internals the rules read - tile, exploration, evaluation,
  army and territory-graph facts; the patch binding, the snapshot cursor facts and the evaluation activation answer on
  the same seam. `GameApi` satisfies it.
- **Script surface** (`ScriptSurface`): the scripted `GameSurface` of the T1 tests - every answer scripted, each
  with an answer for the unanswered case; its call counters make cache serving and the reach of a scan observable.
- **Evaluation writer** (`EvaluationWriter`): puts picks into the running mode's evaluation, and owns that evaluation's
  tail.
- **Load gate** (`Plugin.Awake`): the composition root's load decision, taken before any advisor wiring. It writes
  the one Fatal line and closes the game. It carries no state: a surface that exists is a surface that resolved.
- **Binding pass** (`BindingPass`): the startup resolution of every reflected game member, row by row, each row
  declared with a **binding shape** (`BindingShape`): the argument form the pass binds and, for hot members,
  compiles. It stops at its **first** unresolved row: one drift, one report. A surface built by a returned pass has
  every row bound, so the game surface never re-checks a binding.
- **Game-build drift** (`GameBuildDriftException`): the binding pass stopping on a row the running game build does not
  expose - the game moved a member this mod was compiled against.
- **Supported game build** (`GameBuild.Supported`): the game build the mod was compiled for, stamped by
  the **version reader** from the `Assembly-CSharp.dll` the compiler is given. `GameApi.GameBuild` reports
  the same kind of attribute at runtime, so a load failure line names a matched pair of measurements.
- **Version reader** (`GameBuildReader`): the build-time decode of the game's assembly-level `Version`
  attribute, read through metadata so no assembly is loaded and no game code runs. The plugin build
  stamps with it, `tools/CfgGen` reports the game build it wrote a cfg with, and Humankind-GameLibs'
  derive tool reads the same attribute the same way: one decode, three callers, one number.
- **Running game build** (`GameApi.GameBuild`): the game build the player is on, as the game itself states it. Named
  beside the supported one in the load failure line; the pair's equality picks that line's advice (a mod update when
  the builds differ, an incompatible mod installed beside this one when they match).
- **Load failure line** (`Plugin.FailureLine`): the one Fatal line a stopping binding pass ends in, written by
  `Plugin.Awake` before the game closes: the row, the build pair, and the advice the pair's equality picks.

## Vanilla names

What the game calls these surfaces; the anchors for reading the decompiled tree.

- **Pin**: the game's term for its FIMS placement markers - `Pin<T>`, `PlacementPinsSubset`,
  `PlacementPin`; the gate the mod hooks is `SettlementPlacementPinsSubset.PinMustMeShown`.
- **Tile feedback**: the game's term for a map-tile marker fx - a `TileFeedback` kind started through
  `StartFeedback`; the advised-tile overlay is one.
- **Recommanded**: the game's own recommendation flag (`FimsTier < 3`) and its
  `NumberOfRecommandedPins` quota.
- **Showable**: the `DistrictPlacementEvaluation.ValidTile` field the glossary calls a glyph.
