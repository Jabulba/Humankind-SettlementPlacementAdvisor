# PROTOTYPE: scan rule table (throwaway, ticket 5)

Prototype artifact for wayfinder ticket "Decide the territory scan and claimability rules"
(issue 5 of map issue 1). Throwaway: this is the reactable statement of the agreed scan
rules, not a spec and not implementation. Gate anchors are `file.cs:line` in the decompiled
tree `reference/managed/firstpass/Amplitude.Mercury.Simulation/ArmyActionHelper.cs` unless
marked otherwise. Decision numbering (Q1-Q30) matches the grilling round transcript on the
ticket.

## Scan shape

- Neighborhood: hovered territory = level 1, then `RangeLevels` hops over **all** adjacency
  edges: `Snapshots.World.Territories[t].Adjacencies[].TerritoryIndex`, no
  `TerritoryTransition` filter. Transitions are an AI-layer mobility overlay, not adjacency
  (Q18). `GameApi.LandAdjacentTerritories`'s `Open`-only filter (`GameApi.cs:310-324`) dies.
- Scope: every tile of every neighborhood territory (Q20). No movement points, no zone of
  control, no 2-turn reachability flood fill; vanilla's
  `ArmyActionHelper.FindReachable(...)` step inside
  `ConstructibleHelper.FillValidSettlePositions` (`ConstructibleHelper.cs:2480`) is replaced
  by the neighborhood (Q1, Q19).
- Unknown adjacency: hovered territory only + `GameApi.Fail` log (Q28).
- No evaluation budget, no `MaxRangeTerritories` cap (Q12): `PickComputation.cs:53-57,134-138,
  197-206,223-225` and the no-cache-of-truncated rule are deleted.

## Rule table

| Fact / gate row | Role in the scan |
|---|---|
| decoy `ActionProhibited` 365-370; `ArmyLockedByBattle` 371-374; `ArmyLocked` 375-378; `IsLesserEmpire` 391-395; `IsRented` 396-400; `NoLandUnit` 401-406; `AtLeastOneSeafaringUnit` 407-412; `DiplomatProhibitedAction` 413-417; `TrespassingStatus` 418-421; `MinorEmpireAlreadyHaveSettlement` 422-425; nomad `NotEnoughUnits` 426-434 | **army-level, once per refresh** (army overload with `computeMinimumCost: false`); any hit publishes INACTIVE and skips the scan (Q6, Q24) |
| `NoValidAdjacentTerritoryToClaim` / `NotEnoughMinimumMoney` / `NotEnoughMinimumInfluence` 435-457 | **dropped**: never consulted (Q25) |
| `IsEdgeOfTheWorld` 510-514 | **territory filter**, all modes |
| `Claimed \|\| SettlementGUID != 0` 515-518 | **territory filter**, `CreateCamp` |
| `SettlementGUID != 0` 649-652 | **territory filter**, `FoundCity` |
| `SettlementGUID != 0` except own-empire camp/pre-camp settlement 740-753, replaceability 763-771 | **territory filter**, `FoundCityWithSettler` (Q3, Q27) |
| territory `IsOcean`; `AnyClaimableTile`; `DemilitarizedByEmpireIndex`; close-border bits | **never a filter**: sea-flagged territories host land camps; `AnyClaimableTile` is a map-load AI-brain hint (Q4); demilitarization and close borders are AI heuristics |
| water tile; explored tile with the gate's any-bit predicate `((ExplorationMap \| BattleExplorationMap) & bits) != 0` (`VisibilityController.cs:255-258`), all three modes (Q9, Q26); terrain + POI via `ConstructibleHelper.CheckTerrainAndPOI` 2-arg (`ConstructibleHelper.cs:2824-2828`) | **tile pre-filter**, cheap snapshot reads, runs before the gate: the gate's cost rows compute `GetSettleCost` per tile (491-507), which is the expensive part |
| mode tile gate, membership = `flags == None` minus the mask {`Part1.NotEnoughMoney`, `Part1.NotEnoughInfluence`} (Q5, Q23): camp 465-545; FoundCity 629-672 (clean on unexplored at 638-641, so the explored pre-filter is the advisor's rule); settler 713-777 | **tile gate**, one reflection call per pre-filtered tile, simulation thread, never reentrant |
| FIMS weights Food 1.5 / Industry 1 / rest 0 (`SimulationEvaluator.cs:3649-3660`); resource-tile exclusion (`SimulationEvaluator.cs:2399`) | **scoring**, kept |
| `MinimalFimsForCampPlacement` 25 floor; `IsTileAtTerritoryBorder` (`SimulationEvaluator.cs:3662`); distance-to-army tie-break (`SimulationEvaluator.cs:2404-2411`) | **dropped** |

## Ranking and publication

Per territory: sort by weighted score descending, tile index ascending; admit the clamped
`topCount` tiles, then admit every additional tile tied with the last admitted score (Q22:
quota 2 with 3 tiles tied at position 1 publishes 3; quota 2 with 1 tile at position 1 and 3
tiles tied at position 2 publishes 4). Merged publish set stays ascending by tile index for
the writer's invariant slots (Q14).

## Cadence

Compute every refresh, served by the per-territory cache. Cache key: territory index +
district frame + construction frame + visibility frame + empire index + admitted count +
placement mode + `World.TerritoryInfo.Frame` (Q11, Q21, Q29: claims, unclaims and owner
steals bump that frame at `World.cs:870,2358,2383,2399` and invalidate every cached
neighborhood entry). Per mouse move: cache hits plus at most one newly entered territory.

## Seam and vocabulary

- `GameSurface.LandAdjacentTerritories` -> `AdjacentTerritories` (no transition filter).
- Explored predicate moves from all-bits (`IsWorldPositionExploredFor`, bound at
  `GameApi.cs:110`) to the gate's any-bit form.
- GLOSSARY: delete **Range territories**; keep **Hovered territory** as neighborhood center;
  add **placement neighborhood**, **claimable territory**, **top tie set**, **scan pass**.
- `RangeLevels`, `TopHovered`, `TopNeighbour` config knobs stay (Q17); README and config help
  text lose "Land adjacency only".

## Handoffs

- Issue 10 (perf prototype): rescoped to cached-neighborhood cost, not full-map cost.
- Issue 8 (presentation and write-side contract): unbounded-per-territory tie set; tail
  capacity, `Showable` on every tied tile, pin cap of 3 (`UI/PlacementPinsSubset.cs:11`).
- Issue 7 (config surface): surviving knobs; `test-range.sh` cap/truncation cases die in the
  spec assembly (issue 9).
