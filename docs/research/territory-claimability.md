# Territory-level claimability facts (vanilla)

Research for issue 3 of the wayfinder map (issue 1). Scope: the territory-level facts vanilla
uses around camp claimability (world-edge territories, claimed territories, territory data
structures and adjacency edge types), which of them a mod can query, and how "a territory with
no valid placement tile" is expressible from those facts.

Citations are `file.cs:line` in the gitignored decompiled tree `reference/managed/firstpass/`
(`Amplitude.Mercury.Simulation/`, `Amplitude.Mercury.Interop/`, `Amplitude.Mercury.Interop.AI/`,
`Amplitude.Mercury.Interop.AI.Entities/`). Comments inside decompiled files are local annotations
added in that tree, not game source: code is the authority. Types living in
`Amplitude.Mercury.Data.dll` are named by type, not line: that assembly is not in the firstpass
tree and was read with `scripts/decompile-game.sh <FQTN>`. The tile-level gate (issue 2) is
cataloged in the sibling research branch `research/camp-validity-gate`.

Three access layers appear throughout, and every fact below is assigned to one:

- **sim**: `Amplitude.Mercury.Simulation` types are `internal` (`World.cs:24`, `Territory.cs:10`,
  `ArmyActionHelper.cs:12`, `ConstructibleHelper.cs:14`, `SimulationEvaluator.cs:26`), reached only
  by reflection over `Sandbox` statics (`Sandbox.cs:36` internal; `Sandbox.cs:80` SimulationEvaluator,
  `Sandbox.cs:82` VisibilityController, `Sandbox.cs:114` TerritoryPathfindManager, `Sandbox.cs:262` World).
  This is what `GameApi.Resolve()` does (`GameApi.cs:43-120`).
- **interop**: `Amplitude.Mercury.Interop` public data structs (`TerritoryInfo.cs:10`, `TileInfo.cs`)
  mirrored on the simulation side as frame-versioned arrays (`World.cs:278`).
- **snapshot**: `Amplitude.Mercury.Interop.AI` public AI snapshot: `Snapshots.World`
  (`Snapshots.cs:9`, `:13`) with `WorldSnapshot.Tiles/Territories/TileInfo/TerritoryInfo`
  (`WorldSnapshot.cs:47,49,79,81`). `TileInfo`/`TerritoryInfo` are per-sync copies of the
  simulation arrays (`WorldSnapshot.cs:335-336`), so the snapshot carries the public sync view,
  and `Territories[i]` is the AI territory entity. The mod reads this layer today
  (`GameApi.cs:141-144,252-266,268-288,290-324`).

## 1. Territory data structures

| Fact | Field | Layer | Source |
| --- | --- | --- | --- |
| Territory count / territory object by index | `World.Territories` (internal `Territory[]`) | sim | `World.cs:187` |
| Public per-territory record, frame-versioned | `World.TerritoryInfo` (`ArrayWithFrame<TerritoryInfo>`) | sim | `World.cs:278`; frame bumped on every claim change (`World.cs:870,2358,2383,2399`) |
| Territory index | `Territory.Index`, `TerritoryInfo.TerritoryIndex`, `Territory.TerritoryIndex` (snapshot) | sim / interop / snapshot | `Territory.cs:12`, `TerritoryInfo.cs:53`, `Interop.AI.Entities/Territory.cs:510` |
| Tile list | `Territory.TileIndexes` (sim), `TerritoryInfo.TileIndexes` (interop), `Territory.TileIndexes` (snapshot) | all three | `Territory.cs:22`, `TerritoryInfo.cs:57`, `Interop.AI.Entities/Territory.cs:518`; built from tile scan at load (`World.cs:795-843`), copied to the public record at `World.cs:864` |
| Adjacent territory indexes (no edge type) | `Territory.AdjacentTerritories` (sim), `TerritoryInfo.AdjacentTerritoryIndexes` (interop) | sim / interop | `Territory.cs:24`, `TerritoryInfo.cs:26`; built at `World.cs:803-806,840`, copied at `World.cs:866` |
| Edge type per adjacency | `Territory.Adjacencies[i].Transition` (`TerritoryTransition` flags), `.TerritoryIndex`, `.CityEmpireBits` | snapshot only | `Interop.AI.Entities/Territory.cs:17-25,530`; filled in `ComputeAdjacencies` (`:413-459`); the sim adjacency array carries no type, only indexes |
| Tile -> territory | `TileInfo.TerritoryIndex` (short) | interop + snapshot | `TileInfo.cs:27`; snapshot copy `WorldSnapshot.cs:79,335`; convenience helper `WorldSnapshot.GetTerritory(tileIndex)` (`WorldSnapshot.cs:127`) |
| Continent, biome, ocean flag | `ContinentIndex`, `Biome`, `IsOcean` (sim + snapshot), `TerritoryInfo.ContinentIndex` | sim / interop / snapshot | `Territory.cs:14,16,18`, `Interop.AI.Entities/Territory.cs:512,522`, `TerritoryInfo.cs:61` |
| Owner chain (simulation truth) | `Territory.Region` -> `Region.Settlement` -> `Empire` | sim | `Territory.cs:102`; used as the ownership read in district placement (`ConstructibleHelper.cs:2610-2611,2625-2629`) and trespassing (`DepartmentOfDefense.cs:1083-1086`) |
| Per-empire stealth data on the territory | `Territory.MajorEmpireTerritoryPerEmpireIndex` (`BestDetectionFrom*`, `DetectionForTerritory`) | sim + snapshot | `Territory.cs:128`, `Interop.AI.Entities/MajorEmpireTerritory.cs:21-31`; bound per sync (`Interop.AI.Entities/Territory.cs:254-257`) |
| Demilitarization | `Territory.DemilitarizedByEmpireIndex`, `TerritoryInfo.DemilitarizedByEmpireIndex` (-1 = none) | sim + interop | `Territory.cs:132`, `TerritoryInfo.cs:46`; snapshot helper `IsDemilitarizedByOther` (`Interop.AI.Entities/Territory.cs:676-683`) |
| Trade/movement node graph (separate from the territory graph) | `Sandbox.TerritoryPathfindManager`: `TerritoryNode` (`Type`, `Center`, `Flags`, `AdjacentNodes[].IsBlocked`) | sim | `Sandbox.cs:114`, `TerritoryNode.cs:6,12,14,16,18`, `TerritoryNodeTypes.cs:3-9` (Land/CoastalWater/Oceanic), `TerritoryNodeFlags.cs:6-12` (IsTerritoryMainNode, CanHaveRoundHouseOrJetty, IsEdgeOfTheWorld, HasAnHarborInTerritory), `AdjacentTerritoryNode.cs:5-9`; node flags derived from sim territories at `TerritoryPathfindManager.cs:292-294` |

The snapshot `Territory` also carries derived shape facts used by the AI: `IsOcean`
(`Interop.AI.Entities/Territory.cs:522`), `IsCoastal`, `IsIsland`, `IsArchipelago` (`:538-542`,
computed in `ComputeIsland`, `:289-364`), `BorderTileIndices` (`:572`, border tiles computed at
`:168-188`), `IsFrozenByHumanClaim` (`:562`), `IsBeingTakenAway` (`:566`).

## 2. World-edge territories

There is one flag, and it is public: the simulation territory flag is copied into the public
territory record at load, so every layer sees the same boolean.

| Fact | Field / flag | Layer | Source |
| --- | --- | --- | --- |
| Territory touches the world border | `Territory.IsEdgeOfTheWorld` | sim | `Territory.cs:20`; loaded from the map generator (`World.cs:3209`, from `WorldGenerator.Interop.Territory.IsEdgeOfWorld`), copied to the public record at `World.cs:867` |
| Same fact on the public record | `TerritoryInfo.IsEdgeOfTheWorld` | interop + snapshot | `TerritoryInfo.cs:24`; reached from the snapshot as `territory.Info.IsEdgeOfTheWorld` (`Info` bound per sync, `Interop.AI.Entities/Territory.cs:235`) |
| Camp claim is blocked on such a territory | `ArmyActionFailureFlags_Part1.TerritoryIsEdgeOfTheWorld` | sim gate | `ArmyActionHelper.cs:510-514` (camp tile gate), flag defined `ArmyActionFailureFlags_Part1.cs:56`; the city-founding gate applies the same check (`ArmyActionHelper.cs:645-648`) |
| Neighbors of such territories are skipped in neighbor prerequisites | `CheckNeighbourTilesPrerequisite` | sim | `ConstructibleHelper.cs:3251-3255` |
| Edges toward such territories are impassable in the territory graph | `TerritoryTransition.Closed` | snapshot | `Interop.AI.Entities/Territory.cs:444-448`: if either side is `IsEdgeOfTheWorld` the adjacency transition is set to `Closed` and nothing else is added |
| Army movement paths flag them | `PathArmyActionFailureFlags.IsEdgeOfWorld` | sim | `SimulationEvaluator.cs:803-806` |
| Pathfind nodes carry the flag | `TerritoryNodeFlags.IsEdgeOfTheWorld` | sim | `TerritoryNodeFlags.cs:11`, set at `TerritoryPathfindManager.cs:292-294` |
| Continent-shape edge test | single-territory continent that is edge-of-world is excluded from ocean-shape math | snapshot | `WorldSnapshot.cs:260` |
| World size / wrap context | `WorldSnapshot.WorldWidth`, `.WorldHeight`, `.WorldWrap` | snapshot | `WorldSnapshot.cs:57,59,61` |

`IsEdgeOfTheWorld` is a static map fact: it is written at map load only.

## 3. Claimed territories: by whom, and claimed vs owned

Ownership lives on the public territory record, not on the simulation `Territory` entity: the sim
entity has no owner field, only the `Region` reference (`Territory.cs:102`). All claim fields below
are fields of `Amplitude.Mercury.Interop.TerritoryInfo` (public struct, `TerritoryInfo.cs:10`),
reachable as `Snapshots.World.TerritoryInfo[i]` (`WorldSnapshot.cs:81`) or as
`Snapshots.World.Territories[i].Info` (`Interop.AI.Entities/Territory.cs:536`).

| Fact | Field / flag | Source |
| --- | --- | --- |
| Claimed (has a settlement on it) | `TerritoryInfo.Claimed` | `TerritoryInfo.cs:22`; defaults to unclaimed at map load (`World.cs:718-721`) |
| Owner empire | `TerritoryInfo.EmpireIndex` (byte, `byte.MaxValue` = none) | `TerritoryInfo.cs:14`, default `World.cs:718`; read pattern "claimed ? owner : -1" (`SimulationEvaluator.cs:6581`) |
| Owning settlement | `TerritoryInfo.SettlementGUID`, `.SettlementIndex` (-1) | `TerritoryInfo.cs:16,18`; snapshot resolves the GUID to the AI settlement entity only when claimed (`Interop.AI.Entities/Territory.cs:236-246`) |
| Owner is a city (not a camp) | `TerritoryInfo.IsOwnedByCity` | `TerritoryInfo.cs:20`; set on city attach (`World.cs:2356`, `DepartmentOfTheInterior.cs:3331`), cleared on unclaim (`World.cs:2381`) |
| Settlement identity in the sim truth chain | `Territory.Region` -> `Region.Settlement` | `Territory.cs:102`; set/cleared with the claim (`World.cs:2360,2385`) |
| Explored / patrolled bitmaps per territory | `TerritoryInfo.ExploredByEmpires`, `.PatrolledByEmpires` | `TerritoryInfo.cs:28,30`; patrolling used in visibility math (`VisibilityController.cs:1614,1627`) |
| Demilitarized by an empire | `TerritoryInfo.DemilitarizedByEmpireIndex` | `TerritoryInfo.cs:46` |

Write path (the claim itself), all in `World.cs`:

| Operation | Effect | Source |
| --- | --- | --- |
| `ClaimTerritory(territoryIndex, settlement)` | writes `SettlementGUID`, `SettlementIndex`, `EmpireIndex`, `EmpireColor`, `RegionGUID`, `IsOwnedByCity` (settlement status is `City`), `Claimed = true`, bumps `TerritoryInfo.Frame`, attaches `Territory.Region`, raises `SimulationEvent_TerritoryOwnerChanged` when the empire changed | `World.cs:2345-2370`; called from the interior department at `DepartmentOfTheInterior.cs:9264` |
| `UnclaimTerritory(territoryIndex)` | clears all of the above (`Claimed = false`, `EmpireIndex = byte.MaxValue`, `SettlementIndex = -1`) and detaches `Territory.Region` | `World.cs:2372-2391` |
| `ChangeTerritoryOwner(territoryIndex, newOwnerEmpireIndex)` | changes the owner without changing the settlement link (steal path) | `World.cs:2393-2403` |

Claimed vs owned, as vanilla uses it in the camp gate:

| Rule | Condition | Flag | Source |
| --- | --- | --- | --- |
| Camp: any settlement on the territory blocks | `SettlementGUID != 0 \|\| Claimed` | `Part1.TerritoryAlreadyClaimed` | `ArmyActionHelper.cs:515-518` (flag `ArmyActionFailureFlags_Part1.cs:27`) |
| City founding: only a settlement blocks | `SettlementGUID != 0` (no `Claimed` term) | `Part1.TerritoryAlreadyClaimed` | `ArmyActionHelper.cs:649-652` |
| Cost math: army's own territory is claimable when unclaimed | `!TerritoryInfo.Claimed` | (cost, no flag) | `DepartmentOfTheTreasury.cs:983-992` |
| Cost math: no adjacent territory qualifies | `anyValidAdjacentTerritory == false` | `Part2.NoValidAdjacentTerritoryToClaim` | `ArmyActionHelper.cs:442-446` (flag `ArmyActionFailureFlags_Part2.cs:14`), computed in `DepartmentOfTheTreasury.cs:975-1013` |
| Trespassing depends on territory ownership | army tile -> territory -> owner chain | `Part1.TrespassingStatus` | `DepartmentOfDefense.cs:1012-1015`, `:1083-1086`; camp gate reads it as a non-terminal flag (`ArmyActionHelper.cs:418-421`) |
| Invariant enforced by the game | claimed territory must have a settlement | `InvalidOperationException` | `SimulationEvaluator.cs:6605` |
| Claimed drives presentation color | `Claimed` -> empire color | (no flag) | `World.cs:2337-2340` |

Note on the adjacency cost loop, as decompiled: `GetMinimumSettleCostFromAdjacentTerritories`
returns early when the army's own territory is unclaimed (`DepartmentOfTheTreasury.cs:984-992`), so
the loop at `:993-1011` runs only when it is claimed, and the loop guard tests that same source
territory's `Claimed` flag (`:998`), not the neighbor's. So the "cheapest adjacent claimable
territory" scan marks no neighbor as valid and the army-level gate reports
`NoValidAdjacentTerritoryToClaim` whenever the army stands on a claimed territory.

## 4. Adjacency and edge types

Edge types exist in exactly one place: the snapshot adjacency record, as the `TerritoryTransition`
flags enum in `Amplitude.Mercury.Data.dll`, type `Amplitude.Mercury.Data.AI.TerritoryTransition`
(read with `scripts/decompile-game.sh Amplitude.Mercury.Data.AI.TerritoryTransition`; not a
firstpass-tree file):

| Flag | Value | Meaning |
| --- | --- | --- |
| `None` | 0 | no edge kind |
| `Open` | 1 | ground/ground edge (the land edge the mod steps over) |
| `Coast` | 2 | land/shallow-water pair |
| `ShallowWater` | 4 | shallow water |
| `DeepWater` | 8 | deep water |
| `Cliffs` | 0x10 | elevation step >= 2 between non-water tiles |
| `Mountains` | 0x20 | mountain movement type on a side |
| `Closed` | 0x40 | edge-of-world edge, impassable |

| Fact | Field / flag | Source |
| --- | --- | --- |
| Adjacency record | `Territory.Adjacencies[]` (`TerritoryIndex`, `Transition`, `CityEmpireBits`, `DistanceToFrontiers`) | `Interop.AI.Entities/Territory.cs:17-25,530`; allocated from the sim index list at `:200-216` |
| Transition computation per tile pair | `WorldSnapshot.ComputeTilesTransition(fromTile, toTile)`: coast pair rule, `Max` of the per-tile movement-type mapping (ground -> `Open`, shallow -> `ShallowWater`, deep -> `DeepWater`, mountain -> `Mountains`), elevation >= 2 on non-water pairs raises to `Cliffs` | `WorldSnapshot.cs:183-212` |
| Transition union per adjacency | `Transition \|= computed` | `Interop.AI.Entities/Territory.cs:449-454`; note `Max` is a numeric max, so an `Open` land edge with a cliff step ends as `Cliffs` only, i.e. cliff edges are not `Open` |
| Edge-of-world edges | set to `Closed` alone | `Interop.AI.Entities/Territory.cs:444-448` |
| Ground passability | `Adjacency.CanPassWith(army)`: `Open` needs ground movement; `Coast`/`ShallowWater`/`DeepWater` need water movement; close borders to the far territory's empire block | `Interop.AI.Entities/Territory.cs:27-54`; close-border bits computed in `ComputeCloseBorders` (`:368-386`) |
| Close-border blocking over multi-territory paths | `IndirectAdjacency.IsBorderCrossable(closedEmpireBits)`, `Territory.CityEmpireBits` | `Interop.AI.Entities/Territory.cs:107-119`, `:23,380-384` |
| Frontier distance per adjacency | `FrontierDistance[]` (tile + distance, 0 at touching tiles) | `Interop.AI.Entities/Territory.cs:148-153,210-215,741-790` |
| Aggregate transitions toward one city | `AggregateTransitions(cityGuid)` | `Interop.AI.Entities/Territory.cs:701-715` |
| Count of city-adjacent territories | `ComputeCityAdjacentTerritoryCount()` | `Interop.AI.Entities/Territory.cs:685-699` |
| Territory border test per tile (no adjacency array) | `IsTileAtTerritoryBorder(tileIndex)`: scans the 6 neighbors' `TileInfo.TerritoryIndex` | `SimulationEvaluator.cs:3662-3676` (sim, private; used by the camp suggestion filter at `:2399`) |

Mod mapping today (`GameApi.cs`): `TerritoryOfTile` reads snapshot `TileInfo[].TerritoryIndex`
(`:252-266`), `LandAdjacentTerritories` keeps only `Open` edges (`:290-324`, filter
`LandAdjacentIndexes` `:310-324`), `GetTerritoryFacts` returns `Info.Claimed` and
`territory.TileIndexes` (`:268-288`), `IsTileWater` reads snapshot `Tiles[i].IsWater`
(`:137-151`). Contract and fail-closed rules: `GameSurface.cs:27-30,45-62`, `GLOSSARY.md:18-24,55-62`.

## 5. "A territory with no valid placement tile": three ways vanilla expresses it

| Form | Field / flag | Layer | Source |
| --- | --- | --- | --- |
| Precomputed per territory: at least one tile passes the camp terrain+POI gate | `Territory.AnyClaimableTile` (snapshot) | snapshot | Declared `Interop.AI.Entities/Territory.cs:528`; computed only in `FirstSynchronize` by testing `ConstructibleHelper.CheckTerrainAndPOI(DepartmentOfTheInterior.CampDefinition, tile)` over `TileIndexes` (`:167`, `:182-185`) |
| Live per tile test (what the mod uses) | `ConstructibleHelper.CheckTerrainAndPOI(campDefinition, tileIndex)`, `IsTerrainValid`, `CheckPrerequisite(TerritoryTypePrerequisite)` reading `Territory.IsOcean` | sim | `ConstructibleHelper.cs:2824-2828`, `:2351-2356`, `:3141-3172`, `:1606-1616`; mod wrapper `GameApi.cs:122-135` |
| Territory has a settlement, so every tile is blocked | `TerritoryInfo.Claimed` / `SettlementGUID` | interop + snapshot | `ArmyActionHelper.cs:515-518` (see section 3) |
| Whole territory is unclaimable | `TerritoryInfo.IsEdgeOfTheWorld` | interop + snapshot | `ArmyActionHelper.cs:510-514` (see section 2) |
| No claimable territory adjacent to the army | `anyValidAdjacentTerritory` out flag -> `Part2.NoValidAdjacentTerritoryToClaim` | sim | `ArmyActionHelper.cs:442-446`, `DepartmentOfTheTreasury.cs:975-1013` |
| Suggestion list is empty (no candidate tile survives the gate) | `CampPlacementEvaluation.ValidTiles.Length == 0` -> evaluation returns with no suggestion | sim | `SimulationEvaluator.cs:2332-2337`; candidate builder `ConstructibleHelper.cs:2478-2494` |
| Score/quality exclusions that remove tiles from the suggestion list | resource tile, `IsTileAtTerritoryBorder`, score `< MinimalFimsForCampPlacement` | sim | `SimulationEvaluator.cs:2399-2402`, border test `:3662-3676` |
| AI claim decisions treat the pair (`!Claimed`, `AnyClaimableTile`) as "claimable" | `ExclusionCauses.NotClaimable` on `!AnyClaimableTile` | brain (not mod-accessible, see 6) | `ClaimScores.cs:52-55`, `:109`, `:141`, `:462`; `ClaimableTerritoriesForCityExpansion.cs:54` (`!Info.Claimed && AnyClaimableTile && (!coastalOnly \|\| IsCoastal) && (!IsDemilitarizedByOther \| expansionist)`); army missions use the same pair (`ComputeSpecificMissions.cs:151,165,183`) |

Staleness of the precomputed form: `AnyClaimableTile` is written only in `FirstSynchronize`
(`Interop.AI.Entities/Territory.cs:159-217`) and the territory synchronizer's per-pass
`Synchronize` is empty (`:219-221`); the base contract is one first sync per entity
(`Amplitude.Mercury.AI/AIEntitySynchronizer.cs:28,81`). So it is a map-load fact: it does not
track the terrain changes the game makes at runtime (terraformation rewrites `TileInfo.TerrainType`,
`World.cs:2908-2912`; POI removal `World.cs:2696-2699`), and `CheckTerrainAndPOI` reads live
terrain, POI, biome, cliff and mountain prerequisites (`ConstructibleHelper.cs:3143-3170`). For a
live advisor the per-tile test is authoritative; `AnyClaimableTile` is a cheap map-time hint and
an AI-only signal.

Composition, in gate terms: a territory is claimable for this army when
`!IsEdgeOfTheWorld` and `!Claimed && SettlementGUID == 0` (territory-level, kills all tiles),
and it has a valid placement tile when at least one of its `TileIndexes` passes the terrain,
occupancy and exploration gates (tile-level: `ArmyActionHelper.cs:477,519-544`).

## 6. Facts with no mod-accessible path

| Fact | Why no path |
| --- | --- |
| AI claim/expansion scores and exclusion causes (`TerritoryData` analysis: `ExpansionScore`, `ExclusionCauses.NotClaimable/Unreachable`, `DistanceFromEmpiresWithCloseBorder`) | Classes are public in `Amplitude.Mercury.AI.Brain` (`TerritoryData.cs:17`) but live on a per-player brain instance; the simulation exposes no handle to it, only `Empire.IsAIBrainActivated` (`Empire.cs:136`). The brain runs its own analysis passes on the AI side, not on the simulation thread, and reads the same snapshot the mod reads. The underlying facts (`AnyClaimableTile`, `Info.Claimed`, `Adjacencies`, `IsCoastal`, `IsDemilitarizedByOther`) are reachable; the derived scores are not. |
| Camp-specific per-territory placement validity from `Snapshots.DistrictPlacement` | The API is public (`DistrictPlacementSnapshot.cs:165,176` `TryFindTerritoryValidity`/`TryFindValidTerritories`, `IsTerritoryValid` `:252`), but the per-territory validity bits are populated from settlements' `AvailableConstructions` over each settlement's region territories (`:760-790`, `ValidityPerTerritory` set at `:812-813`, cleared per settlement at `:822-839`). Camp placement has no settlement context, so this snapshot answers "can this city build district X in this territory", not "is this territory campable". |
| Raw map-generator edge flag (`WorldGenerator.Interop.Territory.IsEdgeOfWorld`) | Consumed at import into `Territory.IsEdgeOfTheWorld` (`World.cs:3209`) and published on `TerritoryInfo` (`World.cs:867`); the generator struct itself is not a runtime query target. The downstream flag is reachable, so no fact is lost. |
| `DemilitarizedByEmpireIndex` as a camp-claim rule | Not a rule in the camp gate: the flag is absent from the camp failure path (no `Demilitarized` reference in `ArmyActionHelper.cs`'s camp code, `:385-545`) and the interior department claim path. Only the AI claim heuristics consult it (`ClaimableTerritoriesForCityExpansion.cs:54`). The flag itself is readable (`TerritoryInfo.cs:46`, `Territory.cs:132`). |

Every other fact cataloged above is reachable from the mod: snapshot facts by direct public
access, sim facts by reflection over `Sandbox` statics, matching the existing `GameApi` pattern
(`GameApi.cs:43-120`).
