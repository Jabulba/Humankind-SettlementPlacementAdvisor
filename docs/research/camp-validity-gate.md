# Camp validity gate (vanilla)

Research for issue 2 of the wayfinder map (issue 1). Scope: every check the game runs when
asking "can this army create a camp on this tile", the `ArmyActionFailureFlags` each check
sets, which facts are per-territory, and which members a BepInEx mod can call.

Citations are `file.cs:line` in the gitignored decompiled tree `reference/managed/firstpass/`
(`Amplitude.Mercury.Simulation/`, `Amplitude.Mercury.Interop/`). Comments inside decompiled
files are local annotations added in that tree, not game source: code is the authority.
Types living in `Amplitude.Mercury.Data.dll` are named by type, not line: that assembly is not
in the firstpass tree and was read with `scripts/decompile-game.sh <FQTN>`.

## 1. Who runs the gate

| Entry point | Call site | Behavior |
| --- | --- | --- |
| Suggestion list (activation, district/construction/visibility change) | `SimulationEvaluator.cs:2332` -> `ConstructibleHelper.cs:2478` `FillValidSettlePositions` -> per candidate `ArmyActionHelper.cs:465` | Reachable tiles are gated, and a tile joins the candidate list only when the flag set is empty (`ConstructibleHelper.cs:2488-2492`) |
| Hovered tile (`UpdateCurrentPosition`, every mouse move) | `CampPlacementEvaluation.cs:68` | Gate runs on the cursor tile; when it is clean and not already listed, the tile is appended to `ValidTiles` (`CampPlacementEvaluation.cs:69-82`), growing the array by 10 (`:32`, `:77`) |
| Army path preview (`PathArmyAction.SettleCamp`) | `SimulationEvaluator.cs:999` | Gate fills the path action's failure flags |
| Action availability and UI cost (button/tooltip) | `ArmyActionHelper.cs:180` inside `CanExecute`, `computeMinimumCost: true`; Interop mirror `ArmyCursorSnapshot.cs:375` | Army-level overload only: no target tile, adds the cheapest adjacent claim cost |

Two gate overloads exist. `FillCreateCampFailures(army, ...)` is army-level (`ArmyActionHelper.cs:385-458`),
`FillCreateCampFailures(army, tileIndex, ...)` is the tile gate (`:465-545`) and calls the army-level
one first with `computeMinimumCost: false` (`:468`).

Reachability is not inside the tile gate: it is a separate step in the suggestion path only
(`ConstructibleHelper.cs:2480`). Hover and path-preview paths gate the tile without asking
whether the army can reach it.

## 2. Tile gate, in execution order

| # | Check | Condition | Flag | Terminal | Source |
| --- | --- | --- | --- | --- | --- |
| 1 | Army can act | decoy army | `Part1.ActionProhibited` | no | `ArmyActionHelper.cs:365-370` |
| 2 | Army can act | locked by battle | `Part1.ArmyLockedByBattle` | no | `:371-374` |
| 3 | Army can act | any other lock | `Part1.ArmyLocked` | no | `:375-378` |
| 4 | Empire kind | empire is `LesserEmpire` (vassal) | `Part1.IsLesserEmpire` | yes | `:391-395` |
| 5 | Army kind | `MercenaryIndex >= 0` (hired) | `Part1.IsRented` | yes | `:396-400` |
| 6 | Land only | `AtSea` and every unit Seafaring (tag index 11) | `Part1.NoLandUnit` | yes | `:401-406` |
| 7 | Land only | `AtSea` otherwise | `Part2.AtLeastOneSeafaringUnit` | yes | `:407-412` |
| 8 | Unit tag | `HasUnitTag(Diplomat)` | `Part2.DiplomatProhibitedAction` | yes | `:413-417` |
| 9 | Diplomacy | `TrespassingState == Trespassing` | `Part1.TrespassingStatus` | no | `:418-421` |
| 10 | Minor empire | `MinorEmpire` with `Settlements.Count > 0` | `Part1.MinorEmpireAlreadyHaveSettlement` | no | `:422-425` |
| 11 | Nomads | `IsNomadic` and `EraLevel > 0`: `instantUnitCost = 4`, `Units.Count < 4` | `Part1.NotEnoughUnits` | yes (nomads skip all cost math) | `:426-434`, repeated for the target tile at `:482-488` |
| 12 | UI cost preview | cheapest adjacent claimable territory, unaffordable | `Part2.NoValidAdjacentTerritoryToClaim`, `Part2.NotEnoughMinimumMoney`, `Part2.NotEnoughMinimumInfluence` | no | `:435-457` (army overload only, `computeMinimumCost: true`) |
| 13 | Target tile exists | `tileIndex == -1` | `Part1.PositionNotValid` | no | `:470-473` |
| 14 | Fog | `!IsWorldPositionExplored(tileIndex, majorEmpire.Bits)`, major empires only | `Part1.TargetNotVisible` | yes | `:475-481` |
| 15 | Money path | `GetSettleCost(territory)` unaffordable while `MiscFlags.BuyOutpostWithMoney` | `Part1.NotEnoughMoney` | no | `:491-499` |
| 16 | Influence path | `GetSettleCost(territory)` unaffordable in influence (default) | `Part1.NotEnoughInfluence` | no | `:500-507` |
| 17 | Map edge | `TerritoryInfo.IsEdgeOfTheWorld` | `Part1.TerritoryIsEdgeOfTheWorld` | no | `:510-514` |
| 18 | Territory claim | `TerritoryInfo.SettlementGUID != 0` or `Claimed` | `Part1.TerritoryAlreadyClaimed` | no | `:515-518` |
| 19 | Terrain | `!IsTerrainValid(empire, CampDefinition, tileIndex)` | `Part1.NoPOIAllowed` when `POIPrerequisiteNoResourceDeposit` is set, else `Part1.TerrainNotValid` | no | `:519-530` |
| 20 | District occupancy | district on tile and `!DistrictDefinition.IsReplaceableOrDestroyable` | `Part1.IrreplaceableDistrictAtPosition` | no | `:531-539` |
| 21 | Army occupancy | `ArmyMap[tileIndex]` valid and not this army's GUID | `Part1.TileIsOccupied` | no | `:540-544` |

Suggestion-list membership requires the whole accumulated set to be empty, so every non-terminal
flag above is still a rejection in that path (`ConstructibleHelper.cs:2489`).

The gate has no check for: movement points, zone of control, adjacency to own territory,
empire borders, resource tiles, FIMS value, build time, unit count (except nomads). Those are
pathfinding (`section 4`) or scoring concerns (`CampPlacementEvaluation.cs:14`,
`SimulationEvaluator.cs:2399`).

## 3. Terrain sub-gate

`ConstructibleHelper.IsTerrainValid(empire, districtDefinition, tileIndex, ref DistrictPlacementFailureFlags)`
runs three rules, first failure wins (`ConstructibleHelper.cs:2351-2368`).

Rule 1 `CheckTerrainAndPOI` (`:3141-3172`, private) accumulates every rule below and returns
`failures == None`, so one call reports all terrain problems:

| Rule | Input | `DistrictPlacementFailureFlags` | Lines |
| --- | --- | --- | --- |
| Natural wonder | `TileInfo.EncodedNaturalWonder != 255` (`TileInfo.cs:39`) | `NaturalWonderOnPosition` | `ConstructibleHelper.cs:3144-3147` |
| Minor faction spawn point | `MinorFactionManager.SpawnPointMap[tileIndex] >= 0` | `MinorSpawnPointPosition` | `:3148-3151` |
| Terrain type | camp `TerrainPrerequisite` vs `TileInfo.TerrainType` (wasteland switch, Authorized/Forbidden name list) | `TerrainPrerequisiteWrong` | `:3152-3155`, impl `:1336-1363` |
| Point of interest | camp `PointOfInterestPrerequisite.Constraint` vs `TileInfo.PointOfInterest` (`TileInfo.cs:25`) | `POIPrerequisite*` family (`DistrictPlacementFailureFlags.cs:28-34`) | `:3156`, impl `:1399-1501` |
| River | camp `RiverPrerequisite` vs `TileInfo.HasRiver()` (`TileInfo.cs:70`) | `RiverPrerequisiteShouldBePresent` / `ShouldNotBePresent` | `:3157`, impl `:1534-1547` |
| Biome | camp `BiomePrerequisite` vs `TileInfo.Biome` (`TileInfo.cs:29`) | `BiomePrerequisiteWrong` | `:3158-3161`, impl `:1503-1523` |
| Cliff | neighbour elevation/ridge (`TileInfo.Elevation`, `HasRidge`) | `CliffPrerequisiteWrong` | `:3162-3165`, impl `:1549-1584` |
| Mountain | `TileInfo.HasRidge` (`TileInfo.cs:21`) | `MountainPrerequisiteShouldBePresent` / `ShouldBeNotPresent` | `:3166`, impl `:1586-1604` |
| Territory type | `Territory.IsOcean` vs `TerritoryTypePrerequisite.IsOceanic` | `TerritoryInvalid` | `:3167-3170`, impl `:1606-1616` |

Water is excluded here, not by a dedicated water rule: `DistrictDefinition.TerrainPrerequisite`
defaults to `TerrainPrerequisite.NoWater` (Data assembly, `DistrictDefinition`), so sea tiles fail
the terrain-type rule. The territory ocean flag is a separate optional constraint
(`:1606-1616`), and a sea-flagged territory can still host land camp sites.

Rule 2 `CheckNeighbourTilesPrerequisite` (`:3236-3273`, private): AnyTile/AllTiles/NoTile over the
six neighbours, skipping neighbours in edge-of-world territories.

Rule 3 `CheckExtractor` (`:3071-3139`, private): only applies when
`ExtensionDistrictDefinition.ExtractResource`; a camp returns true at `:3074-3077`. Included in
the gate for the shared code path, inert for the camp definition.

Not part of the camp gate: `CheckNoEnemyArmyAtTileIndex` (`:2970-2996`, district placement only),
borough prerequisites, tile visibility prerequisites, district unicity.

The camp gate collapses this whole set into two army-action flags, and tests the resource-deposit
bit first (`ArmyActionHelper.cs:522-529`), so a tile failing both POI and terrain is reported as
`NoPOIAllowed`.

## 4. Reachability, movement, zone of control

`ArmyActionHelper.FindReachable(empire, army, ref results, isAttack: false, isFriendly: false,
VisibilityStatus.Explored, maxNumberOfTurn: 2, ignoreMovementRatio: true)`
(`ConstructibleHelper.cs:2480`, impl `ArmyActionHelper.cs:46-87`):

1. Build the army's `PathfindContext` (`:52`) and force `MovementRatio = 1` (`:55`): the range is
   counted in movement turns, full budget per turn, not remaining movement points.
2. `Sandbox.PathfindManager.FindReachable(ref context, ref setupData, ref results)` flood fill
   (`:68`, impl `PathfindManager.cs:411-426`).
3. Drop tiles below `Explored` visibility for the empire (`:73-86`, `IsWorldPositionExplored` at `:79`).

Zone of control enters only here. `PathfindManager` keeps a `ZoneOfControl` pool
(`PathfindManager.cs:18`, allocated `:306-315`), the flood fill copies it into the per-call
evaluation data (`:99`, `:419`), and `TransitionEvaluator` adds `ZoneOfControlAdditionalCost = 3`
plus `TransitionFlags.ZoneOfControl` to transitions inside an enemy zone
(`TransitionEvaluator.cs:24`, `:565-566`), honouring `PathfindingFlags.IgnoreHostileZoneOfControl`
(`:483`, `:547`). Camp placement sets no ZOC flag, so enemy ZOC shrinks the 2-turn radius.

`CampPlacementEvaluation.PlacementEvaluationRangeInTurn = 2` is a public static readonly
(`CampPlacementEvaluation.cs:10`).

## 5. Fact level

| Level | Facts | Source |
| --- | --- | --- |
| Territory | `Claimed`, `SettlementGUID`, `IsEdgeOfTheWorld`, `AdjacentTerritoryIndexes` | `TerritoryInfo.cs:16`, `:22`, `:24`, `:26`; read by the gate at `ArmyActionHelper.cs:510-518` |
| Territory | settle cost (RPN over distance to nearest city, own/other belief influence, game speed multiplier, cost modifiers) | `DepartmentOfTheTreasury.cs:1015-1056` |
| Territory | `IsOcean` | `Territory.cs:18` (internal class, public field), used at `ConstructibleHelper.cs:1609-1613` |
| Tile | explored bits | `VisibilityController.cs:255-258` (`ExplorationMap \| BattleExplorationMap`) |
| Tile | terrain type, biome, river, ridge, elevation, natural wonder, POI, territory index | `TileInfo.cs:21-41`, `:70` |
| Tile | district GUID, army GUID | `World.Maps.DistrictMap`, `World.Maps.ArmyMap` (`World.cs:81-90`) |
| Tile | terrain prerequisites, neighbour prerequisites | `ConstructibleHelper.cs:3141-3172`, `:3236-3273` |
| Army | decoy, locks, `AtSea`, unit tags, nomadic, trespassing, GUID | `Army.cs:100`, `:216-228`, `:252`; `UnitCollection.cs:32`; `ArmyActionHelper.cs:365-434` |
| Empire | empire kind, `EraLevel`, `MiscFlags.BuyOutpostWithMoney`, money/influence, vision bits | `ArmyActionHelper.cs:391-457`; `Empire.cs:715`; `MajorEmpire.cs:145-147` |

`TerritoryInfo.ExploredByEmpires` (`TerritoryInfo.cs:28`) is a territory-level exploration mask and
is not used by the camp gate: the gate asks the fog controller per tile.

## 6. Callability from a mod

The gate lives almost entirely in `internal` types. A mod compiled against the GameLibs
assemblies can name the public Interop/Data types directly and must reach everything else
through reflection (Harmony `AccessTools`).

| Member | Assembly | Access | Mod path |
| --- | --- | --- | --- |
| `ArmyActionHelper` (class + `FindReachable`, `FillCreateCampFailures`, `FillCommonArmyFailures`) | Simulation | `internal static` class, `internal static` methods (`ArmyActionHelper.cs:12`, `:46`, `:385`, `:465`) | reflection only |
| `ConstructibleHelper` | Simulation | `internal static` class (`ConstructibleHelper.cs:14`) | reflection; `IsTerrainValid`/`FillValidSettlePositions` public static (`:2351`, `:2478`), `CheckTerrainAndPOI` public 2-arg (`:2824`) and private 3-arg (`:3141`), `CheckPrerequisite` overloads public static (`:1336`, `:1399`, `:1503`, `:1525`, `:1549`, `:1586`, `:1606`) |
| `SimulationEvaluator` | Simulation | `internal` class (`SimulationEvaluator.cs:26`) | reflection; `EvaluateSettlementPlacement`/`EvaluateOneSettlementPlacement` public (`:2328`, `:2419`), `IsTileAtTerritoryBorder` private (`:3662`) |
| `Sandbox` statics (`World`, `VisibilityController`, `PathfindManager`, `SimulationEvaluator`, `SimulationEntityRepository`, `MinorFactionManager`) | Sandbox | `internal` type with public static fields (`Sandbox.cs:36`, `:72`, `:78`, `:80`, `:82`, `:262`) | reflection for the type, direct read of the field value once obtained |
| `World.TileInfo`, `World.TerritoryInfo` | Simulation | `internal` instance fields (`World.cs:276-278`) on an `internal` class (`:24`) | reflection to fetch; the values are public structs (`ArrayWithFrame.cs:7`, `:12`) |
| `World.Maps.DistrictMap`, `ArmyMap` | Simulation | `internal static` nested class with public static arrays (`World.cs:81-90`) | reflection on the nested type |
| `Army`, `UnitCollection`, `Empire`, `MajorEmpire`, `Territory`, `VisibilityController`, `PathfindManager`, `DepartmentOfTheInterior`, `DepartmentOfTheTreasury`, `DepartmentOfCulture` | Simulation | `internal` types (`Army.cs:15`, `UnitCollection.cs:12`, `MajorEmpire.cs:21`, `VisibilityController.cs:13`, `PathfindManager.cs:14`, `DepartmentOfTheInterior.cs:49`, `DepartmentOfTheTreasury.cs:16`, `DepartmentOfCulture.cs:25`, `Territory.cs:10`) | reflection; member access is mixed: `Army.MercenaryIndex` `:84`, `Flags` `:100`, `TrespassingState` `:122`, `IsNomadic` `:228`, `IsLocked` `:216` are internal, `AtSea` `:252` and `EmpireIndex` `:200` are public; `UnitCollection.Units` `:14`, `Empire` `:16`, `WorldPosition` `:30`, `UnitTagAsAbilityAvailability` `:32` are internal, `HasUnitTag` `:151`/`:166` and `IsLockedByBattle` `:119` are public; `MajorEmpire.DepartmentOfTheTreasury` `:43`, `DepartmentOfCulture` `:51`, `Bits` `:145`, `MiscFlags` `:147` internal; `Empire.DepartmentOfTheInterior` `:150`, `Index` `:156`, `Settlements` `:162` internal, `EraLevel` `:715` public |
| `DepartmentOfTheInterior.CampDefinition`, `ArmySettleCampInstantUnitCost` (= 4) | Simulation | `internal static` fields (`DepartmentOfTheInterior.cs:211`, `:235`) | reflection |
| `DepartmentOfTheTreasury.CanAfford`, `GetSettleCost`, `GetMinimumSettleCostFromAdjacentTerritories` | Simulation | public methods on an `internal` class (`DepartmentOfTheTreasury.cs:789`, `:1015`, `:975`) | reflection |
| `DepartmentOfCulture.CanAffordWithInfluence` | Simulation | `internal` method on an `internal` class (`DepartmentOfCulture.cs:106`) | reflection |
| `CampPlacementEvaluation` (`PlacementEvaluationRangeInTurn`, `NumberOfBestCampPlacementToDisplay`, `MinimalFimsForCampPlacement`, `ValidTiles`, `SettleFailure`) | Interop | public class (`CampPlacementEvaluation.cs:8-30`) | direct |
| `ArmyActionFailureFlags` | Interop | public struct (`ArmyActionFailureFlags.cs:6`) with internal fields (`:12`, `:16`), private constructor (`:22`), public implicit conversions (`:28-41`), public `&`, `|`, `~` (`:61-106`), public getters `FirstFailureFlags`/`SecondFailureFlags` (`:18`, `:20`), public `Remove` (`:132`, `:137`) | direct read/compose, never construct from parts |
| `ArmyActionFailureFlags_Part1`, `_Part2`, `DistrictPlacementFailureFlags` | Interop | public `[Flags] enum : ulong` (`ArmyActionFailureFlags_Part1.cs:6`, `_Part2.cs:6`, `DistrictPlacementFailureFlags.cs:6`) | direct |
| `TileInfo`, `TerritoryInfo`, `ArrayWithFrame<T>`, `ReachableResults`, `PathfindContext`, `PathfindSetupData`, `CustomTileIndexArea` | Interop / Simulation | public structs/classes (`TileInfo.cs:7`, `TerritoryInfo.cs:10`, `ArrayWithFrame.cs:7`, `ReachableResults.cs:5`, `PathfindContext.cs:10`, `PathfindSetupData.cs:5`, `CustomTileIndexArea.cs:6`) | direct once the owning value is obtained |
| `DistrictDefinition`, `ExtensionDistrictDefinition` (prerequisite fields, `IsReplaceableOrDestroyable = true` default, `ExtractResource`) | Data | public classes/fields (Data assembly) | direct (the mod already resolves `DistrictDefinition` by name at `GameApi.cs:101`) |

Existing mod surface for reference: `GameApi.Resolve()` binds `Sandbox` fields,
`SimulationEvaluator.EvaluateOneSettlementPlacement`, `DepartmentOfTheInterior.CampDefinition`,
`ConstructibleHelper.CheckTerrainAndPOI` (2-arg), `VisibilityController.IsWorldPositionExploredFor`,
and reads territory/water/adjacency from the public AI snapshot (`GameApi.cs:43-120`).

## 7. Shared mutable scratch state

| State | Owner | Consequence |
| --- | --- | --- |
| `workingReachableResults` | `private static` in `ConstructibleHelper` (`ConstructibleHelper.cs:51`), used at `:2480`, `:2483`, `:2486` | The suggestion list is built in a class-level buffer: two concurrent camp evaluations would overwrite each other |
| `workingReachableResults`, `workingAttackReachableResults`, `tempValidTargets*` | `private static` in `ArmyActionHelper` (`ArmyActionHelper.cs:22-40`) | Same pattern: `FillArmyActionInfo` (`:91-92`) and the cut-forest check (`:559`) reuse them; no reentrancy, simulation thread only |
| `neighbourTileIndexes`, `workingTileArray` | `private static` in `ConstructibleHelper` (`:45`, `:47`) | Neighbour-based rules (`:1370`, `:1554`, `:3218`, `:3243`) write a shared 6-int array; not reentrant |
| `synchronousEvaluationData` | `private` field of `PathfindManager` (`PathfindManager.cs:24`), refilled per call (`:99`, `:419`) | One pathfinding evaluation at a time |
| `validTileIndexes`, `exploitationTileIndexes` | `private` fields of `SimulationEvaluator` (`SimulationEvaluator.cs:223-225`), cleared at `:2331`, read at `:2383` | The instance is a sandbox singleton: a mod calling `EvaluateSettlementPlacement` mutates the same scratch vanilla uses |
| `ValidTiles` array | the shared `CampPlacementEvaluation` | Vanilla appends the hovered tile and resizes by 10 (`CampPlacementEvaluation.cs:32`, `:75-81`); this is the array the advisor's tail window lives in |

A faithful reimplementation therefore runs on the simulation thread, never reentrantly, and never
between the vanilla call and its read of these buffers.

## 8. Faithful reimplementation checklist

Cheapest faithful path: one reflection call to
`ArmyActionHelper.FillCreateCampFailures(army, tileIndex, ref flags, ref money, ref culture, ref units)`
and treat `flags == ArmyActionFailureFlags.None` as valid, mirroring `ConstructibleHelper.cs:2489`.
Add the reachability step only when producing a suggestion list.

If the gate is reimplemented from parts, all of these must be duplicated:

1. Army-side rows 1 to 11 of the table in section 2 (decoy, battle lock, lock, LesserEmpire,
   mercenary, at sea in both branches, Diplomat, trespassing, minor-empire settlement, nomad unit
   burn of 4 when `EraLevel > 0`).
2. Fog test with the gate's predicate: `((ExplorationMap[tile] | BattleExplorationMap[tile]) &
   empire.Bits) != 0` (`VisibilityController.cs:255-258`).
3. Territory facts: `IsEdgeOfTheWorld`, `Claimed`, `SettlementGUID != 0` (`ArmyActionHelper.cs:510-518`).
4. Cost: per-territory settle cost plus `CanAfford` (money) or `CanAffordWithInfluence` (influence),
   selected by `EmpireMiscFlags.BuyOutpostWithMoney`, nomads exempt
   (`ArmyActionHelper.cs:489-507`). The cost itself is not reimplementable cheaply: RPN formula,
   belief influence per empire, distance to nearest region, game speed multiplier, action cost
   modifiers (`DepartmentOfTheTreasury.cs:1015-1056`); call it.
5. Terrain: the whole rule 1 set of section 3 plus the neighbour prerequisite rule, evaluated
   against the live `DepartmentOfTheInterior.CampDefinition` (data-driven constraints, do not
   hardcode them).
6. Occupancy: district present and non-replaceable, other army present
   (`ArmyActionHelper.cs:531-544`).
7. Reachability (suggestion list only): 2 movement turns with a full movement budget, Explored
   filter, ZOC cost included (`ConstructibleHelper.cs:2480`, `ArmyActionHelper.cs:46-87`).

Scoring is not gate work: FIMS weighting, the resource-tile exclusion, the territory-border
exclusion and the FIMS minimum of 25 belong to `EvaluateSettlementPlacement`
(`SimulationEvaluator.cs:2386-2413`, `CampPlacementEvaluation.cs:14`, `SimulationEvaluator.cs:3662`).

## 9. Advisor surface measured against the gate

Current pick filters: `PickComputation.cs:190-216` over `GameApi`
(`GameApi.cs:122-135`, `:137-151`, `:206-220`, `:252-304`).

| Gate rule | Advisor today |
| --- | --- |
| Terrain rules (rule 1, all nine) | covered, via `CheckTerrainAndPOI` 2-arg (`GameApi.cs:104`, `:128`) |
| Tile water | covered as an approximation of the `NoWater` terrain default (`GameApi.cs:137-151`) |
| Explored | covered, with a stricter predicate (surprise 8) |
| Territory claimed | covered (`GameApi.cs:268-288`, `PickComputation.cs:191-192`) |
| Neighbour-tile prerequisite | not covered (`IsTerrainValid` rules 2 and 3 are skipped) |
| Territory `SettlementGUID != 0` | not covered separately (only `Claimed`) |
| Territory `IsEdgeOfTheWorld` | not covered |
| District occupancy | not covered |
| Army occupancy | not covered |
| Settle cost and affordability | not covered |
| Nomad unit burn, LesserEmpire, mercenary, at sea, Diplomat, decoy, locks, trespassing | not covered |
| 2-turn reachability and zone of control | not covered: picks iterate the tiles of range territories (`PickComputation.cs:163-171`), not tiles the army can reach |

## 10. Surprises

1. Affordability is part of the suggestion gate. Money and influence failures are ordinary flags,
   and list membership requires zero flags, so a tile the empire cannot pay for never enters
   `ValidTiles` (`ArmyActionHelper.cs:495-507`, `ConstructibleHelper.cs:2489`).
2. The same is true for trespassing and for a minor empire that already has a settlement: those
   flags are non-terminal for the action, yet they remove the tile from the suggestion list
   (`ArmyActionHelper.cs:418-425`, `ConstructibleHelper.cs:2489`).
3. Fog and cost checks are skipped for non-major empires: the whole block is guarded by
   `entity is MajorEmpire` (`ArmyActionHelper.cs:475`), so a minor empire's camp sites are not
   fog-limited and never cost anything at gate level.
4. Armies at sea can never camp: both branches of the `AtSea` rule return (`ArmyActionHelper.cs:401-412`).
5. Overwriting a camp is legal. The occupancy rule blocks only non-replaceable districts, and
   `DistrictDefinition.IsReplaceableOrDestroyable` defaults to true (`ArmyActionHelper.cs:535`,
   Data assembly `DistrictDefinition`).
6. The placing army on the target tile is not a blocker: the rule compares GUIDs
   (`ArmyActionHelper.cs:541`).
7. `tileIndex == -1` sets `PositionNotValid` and keeps going, dereferencing
   `TileInfo.Data[-1]` at `ArmyActionHelper.cs:474`; callers never pass -1 in the camp paths.
8. The gate's fog test is any-bit (`(map & bits) != 0`, `VisibilityController.cs:255-258`) while the
   advisor uses the all-bits variant `(map & bits) == bits` (`IsWorldPositionExploredFor`,
   `VisibilityController.cs:270-277`, bound at `GameApi.cs:110`). They diverge when an empire's
   vision bits are shared (allied/patrol vision).
9. Gate members are not callable: `ArmyActionHelper` and `ConstructibleHelper` are `internal`
   static classes with `internal`/`private` gate methods, so any reuse is reflection-bound
   (`ArmyActionHelper.cs:12`, `ConstructibleHelper.cs:14`).
10. Shared scratch buffers block reentrancy, and there are two of them (one per helper class:
    `ConstructibleHelper.cs:51`, `ArmyActionHelper.cs:22`), so calling
    `FillValidSettlePositions` from a mod races vanilla's own calls
    (`ArmyActionHelper.cs:91-92`, `ConstructibleHelper.cs:2480`).
11. `ArmyActionFailureFlags` cannot be built from parts by a mod: private constructor, internal
    fields; compose with the public implicit conversions and operators
    (`ArmyActionFailureFlags.cs:22`, `:12`, `:16`, `:28-41`).
12. In the suggestion path the three cost outputs of the gate are aliased to a single throwaway
    `FixedPoint` (`ConstructibleHelper.cs:2482`, `:2488`): costs there are meaningless, only flags
    are used.
13. The hovered tile reaches `ValidTiles` through a second path (`CampPlacementEvaluation.cs:69-82`)
    that resizes the shared array by 10 (`:32`, `:77`): this is the tail the advisor owns.
14. `CheckTerrainAndPOI` never resets its flag accumulator (`ConstructibleHelper.cs:3141-3171`),
    so the gate's POI classification wins whenever the resource-deposit bit is set, even when the
    terrain type also fails (`ArmyActionHelper.cs:522-529`).
15. The gate ignores resource tiles: a resource deposit is rejected only through the camp
    definition's POI constraint, and the resource-tile exclusion exists only in scoring
    (`SimulationEvaluator.cs:2399`).
16. Comments in the reference tree are local annotations and can be wrong: `ArmyActionHelper.cs:403`
    reads "Every unit is a land unit?" while the tested index is `UnitTagAsAbility` 11, which is
    `Seafaring` (Data assembly enum, index 4 is `Nomadic`, matching `Army.IsNomadic` at
    `Army.cs:228`).
