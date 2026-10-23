# Vanilla settlement placement evaluation flow

Research notes for the CampPlacementAdvisor evaluation rewrite. Every claim is cited to the ILSpy
trees under `reference/` (git ignored, never committed). Citation prefixes:

- `Interop/` = `reference/managed/firstpass/Amplitude.Mercury.Interop/`
- `Sim/` = `reference/managed/firstpass/Amplitude.Mercury.Simulation/`
- `Sandbox/` = `reference/managed/firstpass/Amplitude.Mercury.Sandbox/`
- `AI/` = `reference/managed/firstpass/Amplitude.Mercury.AI/`
- `Present/` = `reference/outgame/assembly-csharp/Amplitude.Mercury.Presentation/`
- `UI/` = `reference/outgame/assembly-csharp/Amplitude.Mercury.UI/`
- `UIHelpers/` = `reference/outgame/assembly-csharp/Amplitude.Mercury.UI.Helpers/`

Repo-side anchors are cited as `File.cs:line` at the repo root.

## 1. The state object and the thread channel

`CampPlacementEvaluation : DistrictPlacementEvaluation : BasePlacementEvaluation`.

- Contract members: `CurrentPositionIndex`, abstract `Initialize(CustomTileIndexArea)`, abstract
  `UpdateCurrentPosition(int, StaticString, int, SimulationEntityGUID)` (`Interop/BasePlacementEvaluation.cs:7-11`).
- `DistrictPlacementEvaluation`: `ValidTiles` (`Interop/DistrictPlacementEvaluation.cs:98`), `ValidTileCount` (100),
  `FimsSortedValidTileIndexes` (102), `ConditionalFlagsTileIndexes` (104), `HasSynergySomewhere` (106),
  `GenerateFIMSExploitation` (108), `ExclusionRadius` (110). `ValidTile` fields: `TileIndex` 22, `Resource` 24,
  `OverallFims` 26, `OverallGains` 28, `OverallLosses` 30, `PreviousFimsOnTile` 32, `FimsOnTile` 34,
  `Synergies` 36, `ExploitationTiles` 38, `ExploitationTileCount` 40, `FimsTier` 42, `Showable` 44, `BuildTime` 46;
  `ValidTile.Initialize` resets them and sets `Showable = false` (48-71).
- `CampPlacementEvaluation`: `PlacementEvaluationRangeInTurn = 2` (`Interop/CampPlacementEvaluation.cs:10`),
  `NumberOfBestCampPlacementToDisplay = 2` (12), `MinimalFimsForCampPlacement = 25` (14), `InstantMoneyCost` 16,
  `InstantInfluenceCost` 18, `InstantUnitCost` 20, `SettleFailure` 22, `SettleType` 24, `BestCampPlacementTileIndexes` 26,
  `BestCampPlacementIndexes` 28, `BestCampPlacementScores` 30, `ValidTileReallocationSize = 10` (32).

The state lives in the double-buffered snapshot, not in the evaluator:
`ConstructiblePlacementCursorSnapshot<T>.Data.PlacementEvaluation` (`Interop/ConstructiblePlacementCursorSnapshot.cs:24`)
on `SettlementPlacementCursorSnapshot` (`Interop/SettlementPlacementCursorSnapshot.cs:7`), registered as
`Snapshots.SettlementPlacementCursorSnapshot` (`Interop/Snapshots.cs:82`).

Snapshot machinery (`Interop/Snapshot.cs`):

- Two `Data` buffers, index 0 simulation, index 1 presentation (163-171).
- `Synchronize(int frame)` runs only when the target is Simulation, once per sandbox frame, then flips the target to
  Presentation (248-275). `Synchronize(Data, int)` is the subclass worker (291).
- `Present()` swaps the buffers on the presentation thread and raises `PresentationDataChanged` through
  `CheckPresentationDataChanged()` (204-246).
- Drivers: `Snapshots.Synchronize()` from the sandbox thread inside the AI synchronization step
  (`AI/AISynchronizationController.cs:150`); `Snapshots.Present()` from the presentation update loop
  (`Present/Presentation.cs:291`, also 178 and `Present/InGameView.cs:93,97`).

Consequence: all placement evaluation code, including the full evaluation and the single-tile evaluation, runs on the
sandbox thread against the simulation buffer. Presentation code reads the copied buffer after the swap and reacts to
`PresentationDataChanged`. This matches the mod's PickSet-as-only-channel rule (`AGENTS.md:54-56`).

## 2. Mode entry points (presentation thread)

`SettlementPlacementCursor.SettleType` is the mode surface: `Camp`, `FirstCity`, `AdvancedCity`,
`AdvancedCity_Selection` (`Present/SettlementPlacementCursor.cs:14-20`), mapped to `ArmyActionType.CreateCamp`,
`FoundCity`, `FoundCityWithSettler` (199-207).

- Activation: `PresentationCursorController.ChangeToSettlementPlacementCursor` (`Present/PresentationCursorController.cs:634-646`),
  driven from the army actions panel (`UI/ArmyScreen_ArmyActionsPanel.cs:601-613`) and the settler unit action
  (`UI/ArmyScreen_UnitActions.cs:500-511`).
- `ActivateCursor` subscribes to the snapshot and starts it with the army GUID and cursor position
  (`Present/SettlementPlacementCursor.cs:113-115`), sets the pathfinding request per mode (111), and chooses the mouse
  window: `ShowMouseCursorWindow = settleType != SettleType.Camp` (117).
- `DeactivateCursor` unsubscribes, stops district FIMS, clears the tile feedback, stops the snapshot and the territory
  highlight (`Present/SettlementPlacementCursor.cs:127-131`), then stops camp FIMS (134).
- Cursor moves: `OnPositionChanged` calls `UpdateDistrictPosition` (79), which only stores the new position and
  re-arms the snapshot (`Interop/ConstructiblePlacementCursorSnapshot.cs:43-50`, `Interop/Snapshot.cs:277-281`).
- `PresentationUtils.GetArmyActionTypeFromCursor` maps the cursor back to an action type for the UI
  (`UIHelpers/PresentationUtils.cs:161-178`).

## 3. When the full evaluation fires

`ConstructiblePlacementCursorSnapshot.Synchronize(Data simulationData, int frame)`
(`Interop/ConstructiblePlacementCursorSnapshot.cs:69-132`) does three things per sandbox frame:

1. Refresh gate `flag` (91-104): cursor world position, constructible name, empire index, entity GUID,
   `World.DistrictInfo.Frame` (95), `World.ConstructionInfo.Frame` (96), `VisibilityController.Frame` (97), and the
   army's own tile (101-103). No change: return false, zero work.
2. Full evaluation trigger (116-125): a mask over constructible name, empire index, entity GUID, district frame,
   construction frame, visibility frame, OR a change of the settlement GUID under the cursor (117-118). If any is set:
   `EvaluatePlacement(...)` (120) and stamp the frames (121-124). Cursor movement alone is not in the mask.
3. Always: `PlacementEvaluation.UpdateCurrentPosition(tileIndex, ...)` (128), then refresh the stored position and
   hovered settlement GUID (129-130).

So the full evaluation runs: on activation, on district data change, on construction data change, on visibility change,
on entity/empire/constructible change, on hovered settlement change. Mouse moves go through `UpdateCurrentPosition`
only. `VisibilityController.Frame` is stamped whenever exploration changes
(`Sim/VisibilityController.cs:408,423,646,1060,1515,1590,1649`).

The mode switch itself is `SettlementPlacementCursorSnapshot.EvaluatePlacement`
(`Interop/SettlementPlacementCursorSnapshot.cs:36-71`):

- stamps `placementEvaluation.SettleType = actionType` (43);
- `CreateCamp`: `Sandbox.SimulationEvaluator.EvaluateSettlementPlacement(army, eval, DepartmentOfTheInterior.CampDefinition)` (47);
- `FoundCity`: same with `DepartmentOfTheInterior.CityCenterDefinition` (50);
- `FoundCityWithSettler`: resolves the settler unit (`unit.TagAsAbilities[20]`) and its `SettlerStartingPack`, then
  same with `CityCenterDefinition` plus the starting pack (54-64);
- any other type logs an error (67-69).

Two details: the snapshot's constructible name is `CampDistrictDefinitionName` for CreateCamp and
`CityCenterDistrictDefinitionName` for the city modes (17-21); a `ConstructibleActionDefinition` of type
`CampRelocation` is swapped to `CampDefinition` before evaluation (`Interop/ConstructiblePlacementCursorSnapshot.cs:112-115`).

## 4. The full evaluation

`Sim/SimulationEvaluator.cs:2328-2413` (`EvaluateSettlementPlacement`), on the singleton
`Sandbox.SimulationEvaluator` (`Sandbox/Sandbox.cs:80`). It is stateful: scratch lists `validTileIndexes` (2331),
`descriptors`/`synergyDescriptors` (2346-2347), `exploitationTileIndexes` (2382), and the score table
`settlementScorePonderation` (241).

1. Candidates: `ConstructibleHelper.FillValidSettlePositions` (2332; body `Sim/ConstructibleHelper.cs:2478-2494`) =
   `ArmyActionHelper.FindReachable` within `CampPlacementEvaluation.PlacementEvaluationRangeInTurn` (2 turns),
   `VisibilityStatus.Explored`, `ignoreMovementRatio: true` (`Sim/ConstructibleHelper.cs:2480`), intersected with the
   per-tile gate `ArmyActionHelper.FillCreateCampFailures` (2488).
2. `campPlacementEvaluation.Initialize(validTileIndexes)` (2333) copies the candidates: `ValidTileCount` = candidate
   count, arrays grow only (`Interop/DistrictPlacementEvaluation.cs:130-148`), every `ValidTile.Initialize` (143) sets
   `Showable = false`, `GenerateFIMSExploitation = None` (146). The camp subclass allocates the three best arrays of
   length 2 (2333 via `Interop/CampPlacementEvaluation.cs:34-46`).
3. Early return when there is no candidate (2334-2337).
4. Channels and terrain: `GenerateFIMSExploitation = exploitationRuleDefinition.ExploitedFIMS` (2339); optional
   terrain override from `ReplaceTerrainWith` (2340-2345).
5. Descriptor parse, once for the whole call (2346-2367): only empire-scoped effects (`PropertyToFollow` last entry
   `Empire`, 2356-2362) are accumulated into fixed and percent FIMS.
6. Synergy evaluation over all existing settlements and cities (2368-2378) via
   `EvaluateGainFromSettlementAndEmpire` (3055-3090), then all gain sets are filtered to the exploited channels
   (2379-2381).
7. Per candidate tile: `EvaluateSettlementValidTile` (2384-2387; body 2945-2985): `FimsOnTile` from empire/settlement
   gains plus the tile's own terrain (2947-2956), `ExploitationTiles` records for the exploitation ring
   (2958-2977), `MakeValidTileSums` producing `OverallFims`/`OverallGains`/`OverallLosses` (2982, body 3208),
   `BuildTime = GetOutpostBuildTime` (2984, body 1325-1338).
8. Best-of-2 selection (2388-2412): loop over `ValidTiles.Length`, not `ValidTileCount`, so entries appended by the
   cursor path are scanned too (2389-2391); score = `OverallFims` weighted by `settlementScorePonderation`
   (2393-2397, weights Food 1.5, Industry 1, everything else 0, `Sim/SimulationEvaluator.cs:3649-3660`);
   exclusions: resource tile, territory-border tile (`IsTileAtTerritoryBorder`, 3662), score below
   `MinimalFimsForCampPlacement` (25) (2399-2402); insertion into the 2 best slots with the tie rule "equal score and
   closer to the army" (2404-2411); `ReplaceBestCampPlacementAtIndex` cascades and writes `Showable`
   (`Interop/CampPlacementEvaluation.cs:85-108`).

Fact worth noting for the spec: the `startingPack` parameter of `EvaluateSettlementPlacement` is never read in the
body (2328 vs 2329-2413). The settler starting package does not affect the suggestion scoring.

Fact: `FimsTier` is never computed on the camp path. `ComputeFimsTierForDistrictPlacementEvaluation`
(2277-2321) is called only for district placement (2273) and empire-wide construction (4072). Camp tiles keep
`FimsTier = 0`, and the tile-feedback layer marks any displayed tile as "recommanded" when `FimsTier < 3`
(`Present/DistrictPlacementTileFeedback.cs:249`).

## 5. The hovered tile path

`Interop/CampPlacementEvaluation.cs:48-83` (`UpdateCurrentPosition`):

1. Scan `ValidTiles[0 .. ValidTileCount)` for the hovered tile and set `CurrentPositionIndex` (50-57).
2. Zero the three costs and `SettleFailure` (59-67), then run the tile gate
   `ArmyActionHelper.FillCreateCampFailures(army, tileIndex, ref SettleFailure, ref costs)` (68).
3. Append rule (69-82): when the hovered tile is not a candidate and the gate passes, score it with
   `SimulationEvaluator.EvaluateOneSettlementPlacement` (73) and append it at index `ValidTileCount`, growing the array
   by `ValidTileReallocationSize` (10) when full (75-78), then `ValidTileCount++` (79-80).

`Sim/SimulationEvaluator.cs:2419-2469` (`EvaluateOneSettlementPlacement`) repeats the descriptor parse, the synergy
gains, the channel filter, the exploitation ring and `EvaluateSettlementValidTile` for a single tile, and returns it.
No candidate scan, no best-of-2 update, no `Showable` write.

`GetExtensionAndPackage` (`Interop/CampPlacementEvaluation.cs:110-136`) is the second mode switch: `CampDefinition`
by default (113), `CityCenterDefinition` for FoundCity (115-117), `CityCenterDefinition` plus the settler starting
pack for FoundCityWithSettler (118-134). So the hovered-tile scoring uses the mode's district definition.

Consequences for the write side:

- `ValidTileCount` can grow by exactly one slot beyond the vanilla candidate list, at the append index, and vanilla
  never shrinks it back on cursor moves. Only a new full evaluation resets it (132).
- The full evaluation's `Length` loop (2389) reads stale appended entries, so appended entries must be zero
  initialized or owned by the writer. The mod's fixed tail window and scrub rule (`AGENTS.md:51-53`,
  `EvaluationWriter.cs:14-18,151-169`) model exactly this.

## 6. Consumer map

`ValidTiles` + `ValidTileCount`:

- Stars/ghost tile feedback: `SettlementPlacementCursor.SettlementPlacementCursorSnapshot_DataChanged` passes
  `ValidTiles`, `ValidTileCount`, `CurrentPositionIndex` with `onlyShowShowable: true`
  (`Present/SettlementPlacementCursor.cs:195-196`). `DistrictPlacementTileFeedback.UpdateWith` iterates
  `validTilesCount` and skips non-`Showable` entries (`Present/DistrictPlacementTileFeedback.cs:215-226`), marks
  `Recommanded` from `FimsTier` (249), and renders the selected tile's `FimsOnTile`, `Synergies` and `ExploitationTiles`
  (292-341), pushing them to the FIMS renderer (365-368).
- Pins: `SettlementPlacementPinsSubset.RefreshInfos` calls `SetPresentationInfo(ValidTiles, ValidTileCount)`
  (`UI/SettlementPlacementPinsSubset.cs:46-49`); the base filter requires an explored tile and non-empty
  `OverallGains`/`OverallLosses` (`UI/PlacementPinsSubset.cs:46-67`).
- Evaluation itself: append scan (`Interop/CampPlacementEvaluation.cs:50-57,75-80`), scoring (2384-2412).
- FIMS renderer camp branch: reads only the hovered position and its territory, not the evaluation
  (`Present/PresentationFimsController.cs:419-440`).

`Showable`: written only by `ReplaceBestCampPlacementAtIndex` (`Interop/CampPlacementEvaluation.cs:94,103`) and reset
by `ValidTile.Initialize` (`Interop/DistrictPlacementEvaluation.cs:58`). Read only by the tile-feedback filter
(`Present/DistrictPlacementTileFeedback.cs:223-226`). It is the single top-2 channel for the on-map stars.

`BestCampPlacementTileIndexes`: read only by `SettlementPlacementPinsSubset.PinMustMeShown`
(`UI/SettlementPlacementPinsSubset.cs:35-42`), which shows a pin for the hovered tile when `SettleFailure` is `None`
or `NotEnoughInfluence` (27-33) and for the two best tiles. `BestCampPlacementScores`/`BestCampPlacementIndexes` are
read only inside the evaluator (`Sim/SimulationEvaluator.cs:2405-2406`) and
`ReplaceBestCampPlacementAtIndex` (`Interop/CampPlacementEvaluation.cs:97-107`). The two display channels are
therefore independent: stars follow `Showable`, pins follow `BestCampPlacementTileIndexes`.

`GenerateFIMSExploitation`: written at `Sim/SimulationEvaluator.cs:2339`, read only in the same method (2379-2381).
No presentation consumer exists. The camp FIMS overlay and pin numbers are driven by per-tile `FimsOnTile`,
`OverallGains`/`OverallLosses` and `ExploitationTiles`, not by this field.

`SettleType`: written at `Interop/SettlementPlacementCursorSnapshot.cs:43`; the only vanilla presentation reader is
`UI/PlacementPin.cs:201` (the build-time line is shown only when `SettleType == ArmyActionType.CreateCamp`).

`SettleFailure`: `Present/SettlementPlacementCursor.cs:176` (territory highlight plus camp FIMS start/stop, 177-194),
`UI/SettlementPlacementPinsSubset.cs:29` (hovered pin visibility), `UI/MouseMarkersWindow.cs:347` (the camp-creation
mouse marker text and cost lines, 348-374), and the gate write in `Interop/CampPlacementEvaluation.cs:68`.

`InstantMoneyCost`, `InstantInfluenceCost`, `InstantUnitCost`: `UI/MouseMarkersWindow.cs:344-346` (cost display and
failure localization 350-369), filled in `Interop/CampPlacementEvaluation.cs:65-68`.

`BuildTime`: `UI/PlacementPin.cs:203` (camp pins), produced by `Sim/SimulationEvaluator.cs:1325-1338,2984`.

`CurrentPositionIndex`: `Present/DistrictPlacementTileFeedback.cs:219,292-295` (selected tile rendering),
`UI/PlacementPinsSubset.cs:91-103` with `FindHoveredTile() == -1` for settlement pins
(`UI/SettlementPlacementPinsSubset.cs:51-54`).

Dead fields on this path: `FimsSortedValidTileIndexes` (`Interop/DistrictPlacementEvaluation.cs:102`) has no writer
anywhere in the tree. `HasSynergySomewhere` (106) and `ExclusionRadius` (110) belong to the district-placement and
empire-wide-construction flows (`Sim/SimulationEvaluator.cs:2849,4055`; `Present/TutorialHelper.cs:1907`;
`Present/EmpireWideConstructionPlacementCursor.cs:176`), not to camps.

## 7. Mode divergences

Shared by all three modes: the same snapshot, the same `EvaluateSettlementPlacement` call, the same candidate
generator, the same per-tile gate, the same `Showable`/pins/tile-feedback consumers.

Divergences found:

1. Scoring input: `CampDefinition` for CreateCamp, `CityCenterDefinition` for both city modes
   (`Interop/SettlementPlacementCursorSnapshot.cs:47,50,64`; `Interop/CampPlacementEvaluation.cs:113-117`). The
   district definition drives `ExploitationRuleDefinition` (channels, exploitation ring), `ReplaceTerrainWith` and the
   descriptor set, so per-mode scores differ; FoundCityWithSettler additionally passes a starting pack
   (`Interop/SettlementPlacementCursorSnapshot.cs:54-64`) that the evaluator ignores (see 4.).
2. Mouse marker: the camp marker client is activable only in Camp mode
   (`UI/MouseMarkersWindow.cs:315-324`); city modes show the generic cursor window instead
   (`Present/SettlementPlacementCursor.cs:117`).
3. Pin content: the build-time line is camp-only (`UI/PlacementPin.cs:201-203`).
4. Pathfinding feedback and orders: `PathArmyAction.SettleCamp`, `SettleCity`, `UseSettler`
   (`Present/SettlementPlacementCursor.cs:57-72,111`), mapped to `OrderArmySettle`, `OrderArmyConvertToCity`,
   `OrderUseSettlerAbility` / go-to variants in `Present/BaseArmyCursor.cs:421-427,1415-1457`.
5. Validation gates are mode-specific and separate from the evaluation: the action-list gate switches on
   `ArmyActionType` (`Sim/ArmyActionHelper.cs:172-181`): FoundCity uses `FillFoundCityFailures` (army overload
   583-627), FoundCityWithSettler uses `FillSettlerAbilityFailures` (674-711), CreateCamp uses
   `FillCreateCampFailures` (385-458). The tile gates used by order validation and click dispatch:
   - camp tile gate: `Sim/ArmyActionHelper.cs:465-545`: explored (477-481, hard return), nomadic unit cost or claim
     cost (482-509), edge-of-world and claimed territory (510-518), terrain via `CampDefinition` (519-530),
     non-replaceable district (531-539), foreign army on tile (540-544);
   - FoundCity position gate: 629-672, which returns with no failure flags when the position is unexplored
     (638-641), rejects any claimed territory (649-652) and uses the camp terrain definition (653-657), then
     non-replaceable district (658-666) and foreign army (667-671);
   - settler tile gate: 713-777, which allows settling a territory claimed by this empire when the settlement is a
     camp or being settled (740-753), uses the camp terrain definition (758-762), treats camp and pre-camp
     districts as replaceable (763-771), then foreign army (772-776).
   Execution validators: camp order `Sim/DepartmentOfTheInterior.cs:4869-4901` (gate at 4892) and
   `ProcessOrderArmySettle` (4904, `CreateCampAt` at 4935/4940, body 1850); city order 4815-4845 (gate at 4835) and
   `ProcessOrderArmyConvertToCity` (4847, `CreateCityAt` at 4859, body 1194); settler order 6808-6846 (gate at 6841)
   and `ProcessOrderUseSettlerAbility` (6849, `UseSettlerAbilityAt` at 6879, body 1167, starting package applied
   6881-6882). The go-to variants re-validate on arrival in `Sim/DepartmentOfTransportation.cs:1494-1506` (city/camp)
   and 1608-1612 (settler). `Sim/SimulationEvaluator.cs:999,1029,1036` applies the same gates to `ArmyActionAt`.
6. The placement suggestion flow applies the camp gate in all modes (`Sim/ConstructibleHelper.cs:2488`,
   `Interop/CampPlacementEvaluation.cs:68`), so vanilla's city-mode suggestions use camp rules even though the
   execution gates are stricter (unexplored tile pass-through at 638-641, claimed-territory rules, district
   replaceability). The rewrite must state which gate the suggestion pipeline uses per mode.

## 8. Takeover candidates

The methods a full-replacement pipeline must own, each with the evidence above.

Simulation thread, pipeline core:

1. `Sim/SimulationEvaluator.cs:2328` `EvaluateSettlementPlacement`. Full scan/score/best-of-2 pipeline. Sole callers:
   `Interop/SettlementPlacementCursorSnapshot.cs:47,50,64`.
2. `Interop/SettlementPlacementCursorSnapshot.cs:36` `EvaluatePlacement`. The mode switch and the `SettleType` stamp
   (43). The cleanest single takeover point for all three modes.
3. `Sim/ConstructibleHelper.cs:2478` `FillValidSettlePositions`. Range (2 turns, Explored) plus the camp gate as the
   candidate source. Sole caller `Sim/SimulationEvaluator.cs:2332`. Removed by a full-map scan.
4. `Interop/CampPlacementEvaluation.cs:48` `UpdateCurrentPosition`. Hover gate, single-tile scoring, cursor append.
   Already patched by the mod (`Patches.cs:16-19`); a full replacement owns the append rule (69-82).
5. `Interop/CampPlacementEvaluation.cs:34` `Initialize` plus `Interop/DistrictPlacementEvaluation.cs:130`
   `Initialize`. The array/count/`Showable` reset contract and the only point where `ValidTileCount` is lowered.
   Already patched as the tail reservation point (`Patches.cs:40-56`, `EvaluationWriter.cs:30-34`).
6. `Interop/CampPlacementEvaluation.cs:85` `ReplaceBestCampPlacementAtIndex` together with
   `Sim/SimulationEvaluator.cs:2389-2412`. The top-2 cap and the only `Showable` writer; an unbounded tie set must
   replace both.
7. `Sim/SimulationEvaluator.cs:2419` `EvaluateOneSettlementPlacement`. Per-tile FIMS/exploitation/build-time
   evaluation, the scoring primitive the rewrite keeps (weights, resource-tile rule) or reimplements. Callers:
   `Interop/CampPlacementEvaluation.cs:73` and the mod (`GameApi.cs:69`).
8. `Sim/SimulationEvaluator.cs:3649` `InitSettlementScorePonderation` and field `settlementScorePonderation` (241):
   the weight table the rewrite keeps; read at 2393-2397.
9. `Sim/SimulationEvaluator.cs:3662` `IsTileAtTerritoryBorder` and the `MinimalFimsForCampPlacement` test at 2399:
   the two rules the rewrite drops; both live in the best-of-2 loop.
10. `Sim/ArmyActionHelper.cs:465` `FillCreateCampFailures` (tile overload). The per-tile validity gate shared by the
    candidate scan (`Sim/ConstructibleHelper.cs:2488`), the hover gate (`Interop/CampPlacementEvaluation.cs:68`), the
    action list (`Sim/ArmyActionHelper.cs:180`), `ArmyActionAt` (`Sim/SimulationEvaluator.cs:999`) and the order
    validators (`Sim/DepartmentOfTheInterior.cs:4892`; `Sim/DepartmentOfTransportation.cs:1503`). Reimplement for the
    scan if the rewrite needs a mode-specific gate; leave the execution validators on vanilla semantics so an accepted
    click always executes.

Presentation thread:

11. `UI/SettlementPlacementPinsSubset.cs:25` `PinMustMeShown`. The pin filter over `BestCampPlacementTileIndexes`
    (35-42) plus the hovered-tile rule (27-33). Already patched (`Patches.cs:82-88`).
12. `UI/SettlementPlacementPinsSubset.cs:46` `RefreshInfos`. Pins bind to `ValidTiles`/`ValidTileCount`; a tie set
    must keep index-bound pin rebinding coherent (`UI/PlacementPinsSubset.cs:46-67,69-103`).
13. `Present/SettlementPlacementCursor.cs:172` `SettlementPlacementCursorSnapshot_DataChanged`. The star, territory
    highlight and camp FIMS refresh, and the `onlyShowShowable: true` call (195-196). Owning `Showable` means this
    method becomes the advisor's star display for free.
14. `UI/MouseMarkersWindow.cs:341` `CampCreationClient.Refresh`. Consumes `SettleFailure` and the three instant costs
    (344-347); the pipeline must keep writing them.
15. `UI/PlacementPin.cs:201` build-time branch on `SettleType == CreateCamp`; keep `SettleType` and `BuildTime`
    coherent.
16. `Present/SettlementPlacementCursor.cs:125` `DeactivateCursor`. The clear-on-exit contract (127-131, 134).
    Already patched (`Patches.cs:62-67`).

Reference only, no takeover needed:

- `Interop/ConstructiblePlacementCursorSnapshot.cs:69` `Synchronize`. The trigger set (activation, district frame,
  construction frame, visibility frame, entity/empire/constructible change, hovered settlement change). The rewrite's
  pipeline must be evaluated against these triggers; replacing the evaluation does not require changing it.
- `Interop/Snapshot.cs:163-171,204-281`. The thread and buffer-swap contract; the reason `PickSet` stays the only
  cross-thread channel.

Out of scope for the takeover:

- No AI caller of `EvaluateSettlementPlacement` exists in the decompiled trees; the AI reads `SettleCostByTerritory`
  (`AI/MajorEmpire.cs:166`) and its own district-placement snapshot data. The rewrite is player-facing.
- The scenario editor bypasses the flow: `EditorSettlementPlacementCursor` sends `EditorOrderCreateCampAt`
  directly (`Present/EditorSettlementPlacementCursor.cs:19-27`).

## 9. Facts for the downstream tickets

- Two independent display channels: stars follow `Showable`, pins follow `BestCampPlacementTileIndexes`. An unbounded
  tie set needs `Showable` written for every tied tile and a pin filter that ignores the 2-slot array
  (`Present/DistrictPlacementTileFeedback.cs:223-226`; `UI/SettlementPlacementPinsSubset.cs:35-42`).
- Pin capacity is not 2: `PlacementPinsSubset.NumberOfRecommandedPins = 3` (`UI/PlacementPinsSubset.cs:11`), and the
  base pin filter additionally requires explored tiles and non-empty `OverallGains`/`OverallLosses`
  (`UI/PlacementPinsSubset.cs:48,52`): a pick with zero FIMS deltas gets no pin.
- The "advised" star style is `FimsTier < 3` and camps never set `FimsTier`
  (`Present/DistrictPlacementTileFeedback.cs:249`; `Sim/SimulationEvaluator.cs:2277`), so today every displayed camp
  tile is styled as advised.
- Scoring facts a reimplementation needs: weights Food 1.5, Industry 1, others 0 (`Sim/SimulationEvaluator.cs:3649-3660`);
  exclusions resource tile, territory border, score below 25 (2399); tie-break by distance to the army (2407).
- Candidate range: 2 movement turns, Explored, movement ratio ignored
  (`Interop/CampPlacementEvaluation.cs:10`; `Sim/ConstructibleHelper.cs:2480`).
- The evaluation is sandbox-thread only, stateful on the singleton evaluator, and never re-entrant across threads
  (`AI/AISynchronizationController.cs:150`; `Sim/SimulationEvaluator.cs:2331,2346,2382`).
- The hovered settlement GUID is a full-evaluation trigger (`Interop/ConstructiblePlacementCursorSnapshot.cs:117-118`):
  claims and city changes refresh the suggestion set with a static cursor.
- `UpdateCurrentPosition` runs on every frame where the snapshot refreshes, so per-move cost is bounded by the
  single-tile evaluation, not by the scan.
