using System;
using System.Collections.Generic;
using System.Reflection;
using Amplitude; // FixedPoint lives in namespace Amplitude (assembly Amplitude.Framework.dll).
using Amplitude.Mercury.Data.Simulation; // ArmyActionType, DistrictDefinition
using Amplitude.Mercury.Data.World; // PointOfInterestDefinition, ResourceDepositDefinition
using Amplitude.Mercury.Interop;
using Amplitude.Mercury.Simulation; // SimulationEntityGUID
using Amplitude.Mercury.Interop.AI.Entities; // Territory.Adjacency (the snapshot's territory edges)
using AiSnapshots = Amplitude.Mercury.Interop.AI.Snapshots; // public AI snapshot: territory graph
using SnapshotTerritory = Amplitude.Mercury.Interop.AI.Entities.Territory; // snapshot territory entity
using HarmonyLib;

namespace SettlementPlacementAdvisor
{
    /// <summary>
    /// GameSurface implemented by reflection. Resolve() binds every name in RequiredNames:
    /// Sandbox statics are assigned at sandbox initialization, so values are read lazily. Any
    /// unresolved name leaves the surface not-ready with the full failure list, never a partial
    /// binding. Territory facts, the snapshot territory graph and the district definitions use
    /// the public layers directly; the gates, the terrain rule, the explored predicate and the
    /// scoring primitive are simulation internals and reach the game through reflection.
    /// </summary>
    internal sealed class GameApi : GameSurface
    {
        public static readonly GameApi Shared = new GameApi();

        public static bool ReadyStatic;
        public static string ResolveError = string.Empty;

        // Member labels: the failure-list identity, the binding table key, and the adapter key.
        // Every one of them is a name Resolve() checks at startup, so no member the mod reflects
        // over can be missing from the startup probe (AGENTS: an unresolved name is a load failure).
        private const string SandboxEvaluator = "Sandbox.SimulationEvaluator";
        private const string SandboxRepository = "Sandbox.SimulationEntityRepository";
        private const string SandboxWorld = "Sandbox.World";
        private const string SandboxVisibility = "Sandbox.VisibilityController";
        private const string SandboxEmpires = "Sandbox.Empires";
        private const string RepositoryLookup = "SimulationEntityRepository.TryGetSimulationEntity";
        private const string EvaluateOne = "SimulationEvaluator.EvaluateOneSettlementPlacement";
        private const string Ponderation = "SimulationEvaluator.settlementScorePonderation";
        private const string PonderationInit = "SimulationEvaluator.InitSettlementScorePonderation";
        private const string TerrainAndPoi = "ConstructibleHelper.CheckTerrainAndPOI";
        // The district definitions and gates the mode-policy table names. The names live in
        // ModePolicy (the table is the single source of mode dispatch); the binding table below
        // binds exactly those names, so a row can only name a member the startup probe checked.

        private const string Explored = "VisibilityController.IsWorldPositionExplored";
        private const string WorldTerritoryInfo = "World.TerritoryInfo";
        private const string TerritoryInfoEdge = "TerritoryInfo.IsEdgeOfTheWorld";
        private const string TerritoryInfoClaimed = "TerritoryInfo.Claimed";
        private const string TerritoryInfoSettlement = "TerritoryInfo.SettlementGUID";
        private const string TerritoryInfoEmpire = "TerritoryInfo.EmpireIndex";
        private const string SnapshotTerritoryInfo = "WorldSnapshot.TerritoryInfo";
        private const string SnapshotAdjacencies = "Territory.Adjacencies";
        private const string SnapshotTileIndexes = "Territory.TileIndexes";
        private const string TileToPosition = "WorldPosition.GetHexagonOffsetFromTileIndex";
        private const string ArmyType = "Simulation.Army";

        // The shared army-state rows every army action runs first (ArmyActionHelper.cs:365-378:
        // decoy ActionProhibited, ArmyLockedByBattle, ArmyLocked). The camp and settler army
        // overloads do not call them, so the army gate calls them itself; the FoundCity army
        // overload runs them too, and a flag union of the two calls is what vanilla's own
        // gate assembles (its tile overloads call the shared rows before the mode rows).

        // The tail-reservation call the takeover runs, and the Harmony targets: every one is a
        // reflected member, so every one is startup-checked and the patches bind this table's
        // MethodBases instead of repeating the name strings.
        internal const string EvaluationInitialize = "CampPlacementEvaluation.Initialize(CustomTileIndexArea)";
        internal const string PatchEvaluatePlacement = "Patch.SettlementPlacementCursorSnapshot.EvaluatePlacement";
        internal const string PatchUpdateCurrentPosition = "Patch.CampPlacementEvaluation.UpdateCurrentPosition";
        internal const string PatchDeactivateCursor = "Patch.SettlementPlacementCursor.DeactivateCursor";
        internal const string PatchPinMustMeShown = "Patch.SettlementPlacementPinsSubset.PinMustMeShown";

        // The members the adapter reads off a live object by name: the frame counters, the vision
        // bits, the placing army's empire index, the snapshot's cursor fields. They are bound here
        // like every other name, so every read goes through the startup-checked table.
        private const string WorldDistrictInfo = "World.DistrictInfo";
        private const string WorldConstructionInfo = "World.ConstructionInfo";
        private const string PoolAllocatorFrame = "PoolAllocator.Frame";
        private const string ArrayWithFrameCounter = "ArrayWithFrame.Frame";
        private const string VisibilityFrame = "VisibilityController.Frame";
        private const string EmpireVisionBits = "MajorEmpire.Bits";
        private const string ArmyEmpireIndex = "Army.EmpireIndex";
        internal const string SnapshotActionType = "SettlementPlacementCursorSnapshot.actionType";
        internal const string SnapshotCursorPosition = "ConstructiblePlacementCursorSnapshot.nextConstructiblePosition";
        private const string SnapshotType = "Amplitude.Mercury.Interop.SettlementPlacementCursorSnapshot";

        private const string ArmyTypeName = "Amplitude.Mercury.Simulation.Army";

        /// <summary>
        /// One game member the surface binds by name. Shape null is a field binding, "type" checks
        /// the declaring type alone, "name" binds an overload-free method, "member" binds a field
        /// or a property (the members the adapter reads off a live object), and every other value
        /// selects the overload to match (Shape). The army lookup needs the open generic
        /// instantiated for Army, so it carries its own shape.
        /// </summary>
        private struct NameBinding
        {
            public string Label;
            public string TypeName;
            public string Member;
            public string Shape;
        }

        private static readonly NameBinding[] s_Bindings = new NameBinding[]
        {
            new NameBinding { Label = ArmyType, TypeName = ArmyTypeName, Member = null, Shape = "type" },
            new NameBinding { Label = SandboxEvaluator, TypeName = "Amplitude.Mercury.Sandbox.Sandbox", Member = "SimulationEvaluator", Shape = null },
            new NameBinding { Label = SandboxRepository, TypeName = "Amplitude.Mercury.Sandbox.Sandbox", Member = "SimulationEntityRepository", Shape = null },
            new NameBinding { Label = SandboxWorld, TypeName = "Amplitude.Mercury.Sandbox.Sandbox", Member = "World", Shape = null },
            new NameBinding { Label = SandboxVisibility, TypeName = "Amplitude.Mercury.Sandbox.Sandbox", Member = "VisibilityController", Shape = null },
            new NameBinding { Label = SandboxEmpires, TypeName = "Amplitude.Mercury.Sandbox.Sandbox", Member = "Empires", Shape = null },
            new NameBinding { Label = RepositoryLookup, TypeName = "Amplitude.Mercury.Simulation.SimulationEntityRepository", Member = "TryGetSimulationEntity", Shape = "genericArmy" },
            new NameBinding { Label = EvaluateOne, TypeName = "Amplitude.Mercury.Simulation.SimulationEvaluator", Member = "EvaluateOneSettlementPlacement", Shape = "name" },
            new NameBinding { Label = Ponderation, TypeName = "Amplitude.Mercury.Simulation.SimulationEvaluator", Member = "settlementScorePonderation", Shape = null },
            new NameBinding { Label = PonderationInit, TypeName = "Amplitude.Mercury.Simulation.SimulationEvaluator", Member = "InitSettlementScorePonderation", Shape = "name" },
            new NameBinding { Label = TerrainAndPoi, TypeName = "Amplitude.Mercury.Simulation.ConstructibleHelper", Member = "CheckTerrainAndPOI", Shape = "terrainAndPoi" },
            new NameBinding { Label = ModePolicy.CampDefinitionLabel, TypeName = "Amplitude.Mercury.Simulation.DepartmentOfTheInterior", Member = "CampDefinition", Shape = null },
            new NameBinding { Label = ModePolicy.CityCenterDefinitionLabel, TypeName = "Amplitude.Mercury.Simulation.DepartmentOfTheInterior", Member = "CityCenterDefinition", Shape = null },
            new NameBinding { Label = Explored, TypeName = "Amplitude.Mercury.Simulation.VisibilityController", Member = "IsWorldPositionExplored", Shape = "exploredBits" },
            new NameBinding { Label = WorldTerritoryInfo, TypeName = "Amplitude.Mercury.Simulation.World", Member = "TerritoryInfo", Shape = null },
            new NameBinding { Label = TerritoryInfoEdge, TypeName = "Amplitude.Mercury.Interop.TerritoryInfo", Member = "IsEdgeOfTheWorld", Shape = null },
            new NameBinding { Label = TerritoryInfoClaimed, TypeName = "Amplitude.Mercury.Interop.TerritoryInfo", Member = "Claimed", Shape = null },
            new NameBinding { Label = TerritoryInfoSettlement, TypeName = "Amplitude.Mercury.Interop.TerritoryInfo", Member = "SettlementGUID", Shape = null },
            new NameBinding { Label = TerritoryInfoEmpire, TypeName = "Amplitude.Mercury.Interop.TerritoryInfo", Member = "EmpireIndex", Shape = null },
            new NameBinding { Label = SnapshotTerritoryInfo, TypeName = "Amplitude.Mercury.Interop.AI.WorldSnapshot", Member = "TerritoryInfo", Shape = null },
            new NameBinding { Label = SnapshotAdjacencies, TypeName = "Amplitude.Mercury.Interop.AI.Entities.Territory", Member = "Adjacencies", Shape = null },
            new NameBinding { Label = SnapshotTileIndexes, TypeName = "Amplitude.Mercury.Interop.AI.Entities.Territory", Member = "TileIndexes", Shape = null },
            new NameBinding { Label = ModePolicy.CampTileGateLabel, TypeName = "Amplitude.Mercury.Simulation.ArmyActionHelper", Member = "FillCreateCampFailures", Shape = "campTile" },
            new NameBinding { Label = ModePolicy.CampArmyGateLabel, TypeName = "Amplitude.Mercury.Simulation.ArmyActionHelper", Member = "FillCreateCampFailures", Shape = "campArmy" },
            new NameBinding { Label = ModePolicy.FoundCityArmyGateLabel, TypeName = "Amplitude.Mercury.Simulation.ArmyActionHelper", Member = "FillFoundCityFailures", Shape = "armyFlags" },
            new NameBinding { Label = ModePolicy.FoundCityTileGateLabel, TypeName = "Amplitude.Mercury.Simulation.ArmyActionHelper", Member = "FillFoundCityFailures", Shape = "positionFlags" },
            new NameBinding { Label = ModePolicy.SettlerArmyGateLabel, TypeName = "Amplitude.Mercury.Simulation.ArmyActionHelper", Member = "FillSettlerAbilityFailures", Shape = "armyFlags" },
            new NameBinding { Label = ModePolicy.SettlerTileGateLabel, TypeName = "Amplitude.Mercury.Simulation.ArmyActionHelper", Member = "FillSettlerAbilityFailures", Shape = "tileFlags" },
            new NameBinding { Label = TileToPosition, TypeName = "Amplitude.Mercury.WorldPosition", Member = "GetHexagonOffsetFromTileIndex", Shape = "tileToPosition" },
            new NameBinding { Label = ModePolicy.CommonArmyGateLabel, TypeName = "Amplitude.Mercury.Simulation.ArmyActionHelper", Member = "FillCommonArmyFailures", Shape = "armyFlags" },
            new NameBinding { Label = EvaluationInitialize, TypeName = "Amplitude.Mercury.Interop.CampPlacementEvaluation", Member = "Initialize", Shape = "tailInitialize" },
            new NameBinding { Label = PatchEvaluatePlacement, TypeName = "Amplitude.Mercury.Interop.SettlementPlacementCursorSnapshot", Member = "EvaluatePlacement", Shape = "name" },
            new NameBinding { Label = PatchUpdateCurrentPosition, TypeName = "Amplitude.Mercury.Interop.CampPlacementEvaluation", Member = "UpdateCurrentPosition", Shape = "name" },
            new NameBinding { Label = PatchDeactivateCursor, TypeName = "Amplitude.Mercury.Presentation.SettlementPlacementCursor", Member = "DeactivateCursor", Shape = "name" },
            new NameBinding { Label = PatchPinMustMeShown, TypeName = "Amplitude.Mercury.UI.SettlementPlacementPinsSubset", Member = "PinMustMeShown", Shape = "name" },
            new NameBinding { Label = WorldDistrictInfo, TypeName = "Amplitude.Mercury.Simulation.World", Member = "DistrictInfo", Shape = "member" },
            new NameBinding { Label = WorldConstructionInfo, TypeName = "Amplitude.Mercury.Simulation.World", Member = "ConstructionInfo", Shape = "member" },
            new NameBinding { Label = PoolAllocatorFrame, TypeName = "Amplitude.Mercury.PoolAllocator", Member = "Frame", Shape = "member" },
            new NameBinding { Label = ArrayWithFrameCounter, TypeName = "Amplitude.Mercury.Interop.ArrayWithFrame`1", Member = "Frame", Shape = "member" },
            new NameBinding { Label = VisibilityFrame, TypeName = "Amplitude.Mercury.Simulation.VisibilityController", Member = "Frame", Shape = "member" },
            new NameBinding { Label = EmpireVisionBits, TypeName = "Amplitude.Mercury.Simulation.MajorEmpire", Member = "Bits", Shape = "member" },
            new NameBinding { Label = ArmyEmpireIndex, TypeName = ArmyTypeName, Member = "EmpireIndex", Shape = "member" },
            new NameBinding { Label = SnapshotActionType, TypeName = SnapshotType, Member = "actionType", Shape = "member" },
            new NameBinding { Label = SnapshotCursorPosition, TypeName = SnapshotType, Member = "nextConstructiblePosition", Shape = "member" },
        };

        /// <summary>
        /// Every game member name Resolve() checks, in binding order. It is derived from the
        /// binding table, so a surface member whose game name is missing from the table is
        /// reported by the harness: an unbound name can never be a silent partial binding.
        /// </summary>
        public static readonly string[] RequiredNames = Labels();

        private static readonly HashSet<string> s_LogOnce = new HashSet<string>();
        private static readonly int[] s_NoTiles = new int[0];

        private static Dictionary<string, FieldInfo> s_Fields = new Dictionary<string, FieldInfo>();
        private static Dictionary<string, MethodInfo> s_Methods = new Dictionary<string, MethodInfo>();
        private static Dictionary<string, MemberInfo> s_Members = new Dictionary<string, MemberInfo>();
        private static MethodInfo s_ArmyLookup; // TryGetSimulationEntity instantiated for Army


        public bool Ready => ReadyStatic;

        public static void Resolve()
        {
            var failures = new List<string>();
            try
            {
                var fields = new Dictionary<string, FieldInfo>();
                var methods = new Dictionary<string, MethodInfo>();
                var members = new Dictionary<string, MemberInfo>();
                var armyType = AccessTools.TypeByName(ArmyTypeName);

                foreach (var binding in s_Bindings)
                {
                    var owner = AccessTools.TypeByName(binding.TypeName);
                    // AccessTools.TypeByName resolves a concrete type name; the members the
                    // adapter reads off a live object are inherited, and a generic base resolves
                    // only through an instantiated derived type (see FindInheritedMember).
                    if (owner == null)
                    {
                        failures.Add(binding.Label);
                        continue;
                    }

                    if (binding.Shape == "type") continue; // the type itself is the fact to check

                    if (binding.Shape == null)
                    {
                        var field = AccessTools.Field(owner, binding.Member);
                        if (field == null) failures.Add(binding.Label);
                        else fields[binding.Label] = field;
                        continue;
                    }

                    if (binding.Shape == "member")
                    {
                        // Field or property: the members the adapter reads off a live object.
                        // Base types are walked, because the members a snapshot reads live on its
                        // generic base (ConstructiblePlacementCursorSnapshot<T>), where a plain
                        // name lookup on the concrete type finds nothing.
                        var member = FindInheritedMember(owner, binding.Member);
                        if (member == null) failures.Add(binding.Label);
                        else members[binding.Label] = member;
                        continue;
                    }

                    if (binding.Shape == "genericArmy")
                    {
                        var open = armyType == null ? null : FindOpenGeneric(owner, binding.Member);
                        if (open == null) failures.Add(binding.Label);
                        else
                        {
                            s_ArmyLookup = open.MakeGenericMethod(armyType);
                            methods[binding.Label] = s_ArmyLookup;
                        }
                        continue;
                    }

                    var method = FindMethod(owner, binding.Member, Shape(binding.Shape, armyType));
                    if (method == null) failures.Add(binding.Label);
                    else methods[binding.Label] = method;
                }

                ReadyStatic = failures.Count == 0;
                ResolveError = string.Join("; ", failures);
                s_Fields = fields;
                s_Methods = methods;
                s_Members = members;
            }
            catch (Exception e)
            {
                ReadyStatic = false;
                ResolveError = "exception: " + e.Message;
            }
        }

        /// <summary>
        /// The membership mask (SPEC section 2 step 6): the two affordability failures are
        /// dropped so an unaffordable tile stays in the recommendation set. Every other flag
        /// of both flag parts is untouched.
        /// </summary>
        internal static ArmyActionFailureFlags ApplyMoneyInfluenceMask(ArmyActionFailureFlags failures)
        {
            failures.Remove(ArmyActionFailureFlags_Part1.NotEnoughMoney);
            failures.Remove(ArmyActionFailureFlags_Part1.NotEnoughInfluence);
            return failures;
        }

        public bool IsSettlementSiteTile(object districtDefinition, int tileIndex)
        {
            if (districtDefinition == null) return false;
            try
            {
                return (bool)Method(TerrainAndPoi).Invoke(null, new object[] { districtDefinition, tileIndex });
            }
            catch (Exception e)
            {
                Fail("CheckTerrainAndPOI: " + e.Message);
                return false;
            }
        }

        public bool IsTileWater(int tileIndex)
        {
            try
            {
                var world = AiSnapshots.World;
                var tiles = world == null ? null : world.Tiles;
                if (tiles == null || tileIndex < 0 || tileIndex >= tiles.Length) return true;
                return tiles[tileIndex].IsWater;
            }
            catch (Exception e)
            {
                Fail("tile water lookup: " + e.Message);
                return true;
            }
        }

        /// <summary>
        /// Resource tile: the tile's point of interest resolves to a resource deposit. This is
        /// the fact vanilla writes into ValidTile.Resource (SimulationEvaluator.cs:2691-2694,
        /// World.cs:613) and its settlement scoring excludes (2399). Fail closed: an
        /// unresolvable point of interest reports a resource tile, so the tile is never advised.
        /// </summary>
        public bool IsTileResourceTile(int tileIndex)
        {
            try
            {
                var world = AiSnapshots.World;
                var tileInfo = world == null ? null : world.TileInfo;
                var pointsOfInterest = world == null ? null : world.PointOfInterestDefinitions;
                if (tileInfo == null || pointsOfInterest == null) return true;
                if (tileIndex < 0 || tileIndex >= tileInfo.Length) return true;
                var pointOfInterest = tileInfo[tileIndex].PointOfInterest;
                if (pointOfInterest == byte.MaxValue) return false;
                if (pointOfInterest >= pointsOfInterest.Length) return true;
                return pointsOfInterest[pointOfInterest] is ResourceDepositDefinition;
            }
            catch (Exception e)
            {
                Fail("resource tile lookup: " + e.Message);
                return true;
            }
        }

        /// <summary>
        /// Fail-open on reflection issues. Exploration is monotonic, so a cached pick is only
        /// invalidated by VisibilityFrame when new scouting occurs during a settlement session.
        /// Non-major empires are fog-unlimited in the gate (ArmyActionHelper.cs:475), so a
        /// missing vision-bits read reports explored.
        /// </summary>
        public bool IsTileExplored(int tileIndex, int empireIndex)
        {
            if (empireIndex < 0) return true;
            try
            {
                var visibilityController = Field(SandboxVisibility).GetValue(null);
                if (visibilityController == null) return true;
                var bits = GetEmpireVisionBits(empireIndex);
                if (bits == 0) return true;
                return (bool)Method(Explored).Invoke(visibilityController, new object[] { tileIndex, bits });
            }
            catch (Exception e)
            {
                Fail("IsWorldPositionExplored: " + e.Message);
                return true;
            }
        }

        public int GetArmyEmpireIndex(object army)
        {
            return army == null ? -1 : GetIntMember(army, ArmyEmpireIndex);
        }

        /// <summary>
        /// The district definition the mode-policy row names for the mode: the adapter asks the
        /// startup-bound field the table points at, with no mode switch of its own.
        /// </summary>
        public bool TryGetDistrictDefinition(ArmyActionType mode, out object districtDefinition)
        {
            districtDefinition = null;
            ModeRow row;
            if (!ModePolicy.TryGetRow(mode, out row))
                return false; // no row in the mode-policy table: fail closed
            try
            {
                districtDefinition = Field(row.DefinitionLabel).GetValue(null);
                return districtDefinition != null;
            }
            catch (Exception e)
            {
                Fail("district definition " + row.DefinitionLabel + ": " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// The settler's starting pack is passed as null: the vanilla evaluator never reads it,
        /// so it never reaches the score (vanilla-eval-flow research, section 4).
        /// </summary>
        public bool EvaluateTile(object army, int tileIndex, object districtDefinition, out DistrictPlacementEvaluation.ValidTile validTile, out FixedPoint score)
        {
            validTile = default(DistrictPlacementEvaluation.ValidTile);
            score = FixedPoint.Zero;
            if (army == null || districtDefinition == null) return false;
            try
            {
                var evaluator = Field(SandboxEvaluator).GetValue(null);
                if (evaluator == null) return false;

                var vt = default(DistrictPlacementEvaluation.ValidTile);
                vt.Initialize(tileIndex);
                var args = new object[] { army, vt, tileIndex, districtDefinition, null };
                Method(EvaluateOne).Invoke(evaluator, args);
                vt = (DistrictPlacementEvaluation.ValidTile)args[1];

                var fims = vt.OverallFims;
                var ponderation = SettlementWeightTable(evaluator);
                fims.Multiply(ref ponderation);

                validTile = vt;
                score = fims.GetTotalFims();
                return true;
            }
            catch (Exception e)
            {
                Fail("EvaluateOneSettlementPlacement: " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// The vanilla settlement weight table (Food 1.5, Industry 1, rest zero,
        /// SimulationEvaluator.cs:3649-3660). Vanilla initializes it inside its full evaluation
        /// (2396-2398); with that evaluation replaced, the advisor's scoring is the only caller
        /// left, so it initializes the table itself when it is still empty.
        /// </summary>
        private static FimsInfo SettlementWeightTable(object evaluator)
        {
            var ponderation = (FimsInfo)Field(Ponderation).GetValue(evaluator);
            if (!ponderation.IsEmpty) return ponderation;
            Method(PonderationInit).Invoke(evaluator, null);
            return (FimsInfo)Field(Ponderation).GetValue(evaluator);
        }

        public bool GetCurrentFrames(out int districtFrame, out int constructionFrame, out int visibilityFrame)
        {
            districtFrame = constructionFrame = visibilityFrame = 0;
            try
            {
                var world = Field(SandboxWorld).GetValue(null);
                var visibilityController = Field(SandboxVisibility).GetValue(null);
                if (world == null || visibilityController == null) return false;

                var districtInfo = MemberValue(world, WorldDistrictInfo);
                var constructionInfo = MemberValue(world, WorldConstructionInfo);
                if (districtInfo == null || constructionInfo == null) return false;

                districtFrame = GetIntMember(districtInfo, PoolAllocatorFrame);
                constructionFrame = GetIntMember(constructionInfo, PoolAllocatorFrame);
                visibilityFrame = GetIntMember(visibilityController, VisibilityFrame);
                return true;
            }
            catch (Exception e)
            {
                Fail("frame counters: " + e.Message);
                return false;
            }
        }

        public bool GetTerritoryFrame(out int territoryFrame)
        {
            territoryFrame = 0;
            try
            {
                var world = Field(SandboxWorld).GetValue(null);
                if (world == null) return false;
                var territoryInfo = Field(WorldTerritoryInfo).GetValue(world);
                if (territoryInfo == null) return false;
                var frame = MemberValue(territoryInfo, ArrayWithFrameCounter);
                if (!(frame is int)) return false;
                territoryFrame = (int)frame;
                return true;
            }
            catch (Exception e)
            {
                Fail("claim/exploration frame: " + e.Message);
                return false;
            }
        }

        public object TryGetArmy(SimulationEntityGUID guid)
        {
            try
            {
                var repository = Field(SandboxRepository).GetValue(null);
                if (repository == null) return null;
                var args = new object[] { guid, null };
                var ok = (bool)s_ArmyLookup.Invoke(repository, args);
                return ok ? args[1] : null;
            }
            catch (Exception e)
            {
                Fail("TryGetSimulationEntity: " + e.Message);
                return null;
            }
        }

        public int TerritoryOfTile(int tileIndex)
        {
            try
            {
                var world = AiSnapshots.World;
                var tileInfo = world == null ? null : world.TileInfo;
                if (tileInfo == null || tileIndex < 0 || tileIndex >= tileInfo.Length) return -1;
                return tileInfo[tileIndex].TerritoryIndex;
            }
            catch (Exception e)
            {
                Fail("TileInfo territory lookup: " + e.Message);
                return -1;
            }
        }

        public TerritoryFactsResult GetTerritoryFacts(int territoryIndex, out TerritoryFacts facts)
        {
            facts = default(TerritoryFacts);
            facts.Tiles = s_NoTiles;
            try
            {
                var world = AiSnapshots.World;
                var infos = world == null ? null : world.TerritoryInfo;
                var territories = world == null ? null : world.Territories;
                if (infos == null || territories == null
                    || territoryIndex < 0 || territoryIndex >= infos.Length || territoryIndex >= territories.Length)
                    return TerritoryFactsResult.Unknown;

                var info = infos[territoryIndex];
                facts.IsEdgeOfTheWorld = info.IsEdgeOfTheWorld;
                facts.Claimed = info.Claimed;
                facts.HasSettlement = info.SettlementGUID != 0UL;
                facts.IsOwnedByCity = info.IsOwnedByCity;
                facts.OwnerEmpireIndex = info.EmpireIndex == byte.MaxValue ? -1 : (int)info.EmpireIndex;
                var siteTiles = territories[territoryIndex].TileIndexes;
                if (siteTiles != null) facts.Tiles = siteTiles;
                return TerritoryFactsResult.Known;
            }
            catch (Exception e)
            {
                Fail("territory facts: " + e.Message);
                return TerritoryFactsResult.Unknown;
            }
        }

        public bool TryGetAdjacentTerritories(int territoryIndex, out SnapshotTerritory.Adjacency[] edges)
        {
            edges = null;
            try
            {
                var world = AiSnapshots.World;
                var territories = world == null ? null : world.Territories;
                if (territories == null || territoryIndex < 0 || territoryIndex >= territories.Length)
                    return false;
                var reported = territories[territoryIndex].Adjacencies;
                if (reported == null) return false; // no edge set on the snapshot entity
                edges = reported; // every edge: no transition filter here
                return true;
            }
            catch (Exception e)
            {
                Fail("territory adjacencies: " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// The mode's tile gate, dispatched by the mode-policy row: the row names the gate member
        /// and its calling convention, and the adapter only marshals that shape. No mode switch
        /// lives here (GLOSSARY: the mode-policy table is the single source).
        /// </summary>
        public bool TryGetTileGateFailures(ArmyActionType mode, object army, int tileIndex, bool maskMoneyAndInfluence, out ArmyActionFailureFlags failures, out FixedPoint moneyCost, out FixedPoint influenceCost, out FixedPoint unitCost)
        {
            failures = ArmyActionFailureFlags.None;
            moneyCost = FixedPoint.Zero;
            influenceCost = FixedPoint.Zero;
            unitCost = FixedPoint.Zero;
            if (army == null) return false;

            ModeRow row;
            if (!ModePolicy.TryGetRow(mode, out row))
                return false; // no row in the mode-policy table: fail closed

            try
            {
                var args = GateArguments(row.TileGateShape, army, tileIndex, failures, moneyCost, influenceCost, unitCost);
                if (args == null) return false; // the row names a shape the adapter cannot marshal
                Method(row.TileGateLabel).Invoke(null, args);
                failures = (ArmyActionFailureFlags)args[2];
                if (row.TileGateShape == "campTile")
                {
                    moneyCost = (FixedPoint)args[3];
                    influenceCost = (FixedPoint)args[4];
                    unitCost = (FixedPoint)args[5];
                }
            }
            catch (Exception e)
            {
                Fail("tile gate " + mode + ": " + e.Message);
                return false;
            }

            if (maskMoneyAndInfluence) failures = ApplyMoneyInfluenceMask(failures);
            return true;
        }

        /// <summary>
        /// The mode's army-level gate: every army gate the mode-policy row names, in its order,
        /// accumulating into one flag set - the shared army-state rows (decoy, battle lock, lock)
        /// first, then the mode's own army rows, which is what vanilla's own gates assemble. Ask
        /// with computeMinimumCost false: no cheapest-adjacent-claim cost math.
        /// </summary>
        public bool TryGetArmyGateFailures(ArmyActionType mode, object army, out ArmyActionFailureFlags failures)
        {
            failures = ArmyActionFailureFlags.None;
            if (army == null) return false;

            ModeRow row;
            if (!ModePolicy.TryGetRow(mode, out row))
                return false; // no row in the mode-policy table: fail closed

            try
            {
                for (int i = 0; i < row.ArmyGateLabels.Length; i++)
                {
                    var args = GateArguments(row.ArmyGateShapes[i], army, -1, failures,
                        FixedPoint.Zero, FixedPoint.Zero, FixedPoint.Zero);
                    if (args == null) return false; // the row names a shape the adapter cannot marshal
                    Method(row.ArmyGateLabels[i]).Invoke(null, args);
                    failures = (ArmyActionFailureFlags)args[1]; // each row adds its flags to the union
                }
            }
            catch (Exception e)
            {
                Fail("army gate " + mode + ": " + e.Message);
                return false;
            }
            return true;
        }

        /// <summary>
        /// The gate calling conventions, as the game declares them: the flags argument is always
        /// index 1 (army gates) or 2 (tile gates), and only the camp tile gate carries the settle
        /// cost trio. Shapes are the same names the binding table matches overloads by.
        /// </summary>
        private static object[] GateArguments(string shape, object army, int tileIndex, ArmyActionFailureFlags failures, FixedPoint moneyCost, FixedPoint influenceCost, FixedPoint unitCost)
        {
            switch (shape)
            {
                case "campTile": return new object[] { army, tileIndex, failures, moneyCost, influenceCost, unitCost };
                case "positionFlags": return new object[] { army, PositionForTile(tileIndex), failures };
                case "tileFlags": return new object[] { army, tileIndex, failures };
                case "campArmy": return new object[] { army, failures, moneyCost, influenceCost, unitCost, false };
                case "armyFlags": return new object[] { army, failures };
                default: return null;
            }
        }

        /// <summary>Log each distinct advisor failure once.</summary>
        public static void Fail(string what)
        {
            if (Plugin.Instance == null) return;
            lock (s_LogOnce)
            {
                if (s_LogOnce.Add(what)) Plugin.Instance.Log("advisor failure: " + what);
            }
        }

        private static string[] Labels()
        {
            var labels = new string[s_Bindings.Length];
            for (int i = 0; i < s_Bindings.Length; i++) labels[i] = s_Bindings[i].Label;
            return labels;
        }

        private static FieldInfo Field(string label)
        {
            FieldInfo field;
            return s_Fields.TryGetValue(label, out field) ? field : null;
        }

        internal static MethodInfo Method(string label)
        {
            MethodInfo method;
            return s_Methods.TryGetValue(label, out method) ? method : null;
        }

        /// <summary>
        /// The Harmony target of a patch, taken from the startup-bound table so the patch surface
        /// and the startup probe read one list of names. Null means the name was never bound:
        /// Resolve checked every entry in the table and LoadGate refuses to load an unresolved one,
        /// so this is the unreachable safety net, not a degraded path.
        /// </summary>
        internal static MethodBase BoundTarget(string label)
        {
            var method = Method(label);
            if (method == null) Fail("unbound patch target " + label);
            return method;
        }

        /// <summary>
        /// Read a startup-bound member (field or property) off a live object: the frame counters,
        /// the vision bits, the army's empire index, the snapshot's cursor fields.
        /// </summary>
        internal static object BoundMemberValue(object target, string label)
        {
            return MemberValue(target, label);
        }

        private static object MemberValue(object target, string label)
        {
            MemberInfo member;
            if (!s_Members.TryGetValue(label, out member))
            {
                Fail("unbound member " + label);
                return null;
            }
            var field = member as FieldInfo;
            if (field != null) return field.GetValue(target);
            var property = member as PropertyInfo;
            if (property == null) return null;
            return property.GetValue(target, null);
        }

        /// <summary>Parameter shapes of the reflected gates, in the game's own signatures.</summary>
        private static Type[] Shape(string shape, Type armyType)
        {
            var flagsRef = typeof(ArmyActionFailureFlags).MakeByRefType();
            var costRef = typeof(FixedPoint).MakeByRefType();
            switch (shape)
            {
                case "name": return null;
                case "campTile": return new[] { armyType, typeof(int), flagsRef, costRef, costRef, costRef };
                case "campArmy": return new[] { armyType, flagsRef, costRef, costRef, costRef, typeof(bool) };
                case "armyFlags": return new[] { armyType, flagsRef };
                case "positionFlags": return new[] { armyType, typeof(Amplitude.Mercury.WorldPosition), flagsRef };
                case "tileFlags": return new[] { armyType, typeof(int), flagsRef };
                case "terrainAndPoi": return new[] { typeof(DistrictDefinition), typeof(int) };
                case "exploredBits": return new[] { typeof(int), typeof(int) };
                case "tileToPosition": return new[] { typeof(int) };
                case "tailInitialize": return new[] { typeof(CustomTileIndexArea) };
                default: return null;
            }
        }

        private static MethodInfo FindMethod(Type owner, string member, Type[] parameterTypes)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
            foreach (var method in owner.GetMethods(flags))
            {
                if (method.Name != member) continue;
                if (parameterTypes == null) return method;
                var parameters = method.GetParameters();
                if (parameters.Length != parameterTypes.Length) continue;
                bool match = true;
                for (int i = 0; i < parameterTypes.Length; i++)
                {
                    if (parameters[i].ParameterType != parameterTypes[i]) { match = false; break; }
                }
                if (match) return method;
            }
            return null;
        }

        /// <summary>
        /// The startup-bound member lookup: field or property, walking the base chain. The
        /// snapshot's cursor member is declared on its generic base
        /// (ConstructiblePlacementCursorSnapshot&lt;T&gt;), and a constructed generic type exposes
        /// the fields its generic definition declares, so the walk reaches members a plain name
        /// lookup on the concrete type cannot see. An unresolved member is the documented load
        /// failure, never a mid-game fallback.
        /// </summary>
        private static MemberInfo FindInheritedMember(Type owner, string name)
        {
            for (var type = owner; type != null; type = type.BaseType)
            {
                var member = (MemberInfo) AccessTools.Field(type, name) ?? AccessTools.Property(type, name);
                if (member != null) return member;
            }
            return null;
        }

        private static MethodInfo FindOpenGeneric(Type owner, string member)
        {
            foreach (var method in owner.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (!method.IsGenericMethodDefinition || method.Name != member) continue;
                var parameters = method.GetParameters();
                if (parameters.Length == 2 && parameters[0].ParameterType == typeof(SimulationEntityGUID) && parameters[1].ParameterType.IsByRef)
                    return method;
            }
            return null;
        }

        /// <summary>
        /// The empire's shared vision bits, the second argument of the gate's explored
        /// predicate. MajorEmpire only: non-major empires declare no Bits and the gate skips
        /// their fog test, so 0 reports explored.
        /// </summary>
        private static int GetEmpireVisionBits(int empireIndex)
        {
            var empires = Field(SandboxEmpires).GetValue(null) as Array;
            if (empires == null || empireIndex < 0 || empireIndex >= empires.Length) return 0;
            var empire = empires.GetValue(empireIndex);
            if (empire == null) return 0;
            return GetIntMember(empire, EmpireVisionBits);
        }

        /// <summary>WorldPosition for a tile index, as the FoundCity position gate takes it.</summary>
        private static object PositionForTile(int tileIndex)
        {
            var toPosition = Method(TileToPosition);
            var offset = toPosition.Invoke(null, new object[] { tileIndex });
            return Activator.CreateInstance(toPosition.DeclaringType, offset);
        }

        private static int GetIntMember(object target, string label)
        {
            var value = MemberValue(target, label);
            return value is int ? (int)value : 0;
        }
    }
}
