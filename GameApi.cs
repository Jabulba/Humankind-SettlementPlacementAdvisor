using System;
using System.Collections.Generic;
using System.Reflection;
using Amplitude; // FixedPoint lives in namespace Amplitude (assembly Amplitude.Framework.dll).
using Amplitude.Mercury.Interop;
using Amplitude.Mercury.Simulation; // SimulationEntityGUID
using Amplitude.Mercury.Interop.AI.Entities; // Territory.Adjacency (the snapshot's territory edges)
using AiSnapshots = Amplitude.Mercury.Interop.AI.Snapshots; // public AI snapshot: territory graph
using TerritoryTransition = Amplitude.Mercury.Data.AI.TerritoryTransition; // edge transition flags
using HarmonyLib;

namespace SettlementPlacementAdvisor
{
    /// <summary>
    /// GameSurface implemented by reflection. Resolve() only verifies that members exist:
    /// Sandbox statics are assigned at sandbox initialization, so values are read lazily. Any
    /// missing member disables the advisor and is logged verbatim. Territory facts use the
    /// public AI snapshot directly.
    /// </summary>
    internal sealed class GameApi : GameSurface
    {
        public static readonly GameApi Shared = new GameApi();

        public static bool ReadyStatic;
        public static string ResolveError = string.Empty;

        private static FieldInfo s_EvaluatorField;
        private static FieldInfo s_RepositoryField;
        private static FieldInfo s_WorldField;
        private static FieldInfo s_VisibilityField;
        private static FieldInfo s_SettlementDefinitionField;
        private static FieldInfo s_PonderationField;
        private static MethodInfo s_EvaluateOne;
        private static MethodInfo s_TryGetArmy;
        private static MethodInfo s_CheckTerrainAndPoi;
        private static MethodInfo s_IsExploredFor;

        private static readonly HashSet<string> s_LogOnce = new HashSet<string>();
        private static readonly int[] s_NoTiles = new int[0];

        public bool Ready => ReadyStatic;

        public static void Resolve()
        {
            var failures = new List<string>();
            try
            {
                var sandboxType = AccessTools.TypeByName("Amplitude.Mercury.Sandbox.Sandbox");
                if (sandboxType == null)
                {
                    failures.Add("Sandbox type");
                }
                else
                {
                    s_EvaluatorField = AccessTools.Field(sandboxType, "SimulationEvaluator");
                    if (s_EvaluatorField == null) failures.Add("Sandbox.SimulationEvaluator");
                    s_RepositoryField = AccessTools.Field(sandboxType, "SimulationEntityRepository");
                    if (s_RepositoryField == null) failures.Add("Sandbox.SimulationEntityRepository");
                    s_WorldField = AccessTools.Field(sandboxType, "World");
                    if (s_WorldField == null) failures.Add("Sandbox.World");
                    s_VisibilityField = AccessTools.Field(sandboxType, "VisibilityController");
                    if (s_VisibilityField == null) failures.Add("Sandbox.VisibilityController");
                }

                var evaluatorType = AccessTools.TypeByName("Amplitude.Mercury.Simulation.SimulationEvaluator");
                if (evaluatorType == null) failures.Add("SimulationEvaluator type");
                else
                {
                    s_EvaluateOne = AccessTools.Method(evaluatorType, "EvaluateOneSettlementPlacement");
                    if (s_EvaluateOne == null) failures.Add("SimulationEvaluator.EvaluateOneSettlementPlacement");
                    s_PonderationField = AccessTools.Field(evaluatorType, "settlementScorePonderation");
                    if (s_PonderationField == null) failures.Add("SimulationEvaluator.settlementScorePonderation");
                }

                var repositoryType = AccessTools.TypeByName("Amplitude.Mercury.Simulation.SimulationEntityRepository");
                var armyType = AccessTools.TypeByName("Amplitude.Mercury.Simulation.Army");
                if (repositoryType == null) failures.Add("SimulationEntityRepository type");
                else if (armyType == null) failures.Add("Simulation.Army");
                else
                {
                    MethodInfo candidate = null;
                    foreach (var m in repositoryType.GetMethods())
                    {
                        if (!m.IsGenericMethodDefinition || m.Name != "TryGetSimulationEntity") continue;
                        var ps = m.GetParameters();
                        if (ps.Length == 2 && ps[0].ParameterType == typeof(SimulationEntityGUID) && ps[1].ParameterType.IsByRef)
                        {
                            candidate = m;
                            break;
                        }
                    }
                    if (candidate == null) failures.Add("SimulationEntityRepository.TryGetSimulationEntity");
                    else s_TryGetArmy = candidate.MakeGenericMethod(armyType);
                }

                var departmentType = AccessTools.TypeByName("Amplitude.Mercury.Simulation.DepartmentOfTheInterior");
                s_SettlementDefinitionField = departmentType == null ? null : AccessTools.Field(departmentType, "CampDefinition");
                if (s_SettlementDefinitionField == null) failures.Add("DepartmentOfTheInterior.CampDefinition");

                var helperType = AccessTools.TypeByName("Amplitude.Mercury.Simulation.ConstructibleHelper");
                var districtDefinitionType = AccessTools.TypeByName("Amplitude.Mercury.Data.Simulation.DistrictDefinition");
                s_CheckTerrainAndPoi = helperType == null || districtDefinitionType == null
                    ? null
                    : AccessTools.Method(helperType, "CheckTerrainAndPOI", new[] { districtDefinitionType, typeof(int) }, null);
                if (s_CheckTerrainAndPoi == null) failures.Add("ConstructibleHelper.CheckTerrainAndPOI");

                var visibilityType = AccessTools.TypeByName("Amplitude.Mercury.Simulation.VisibilityController");
                s_IsExploredFor = visibilityType == null
                    ? null
                    : AccessTools.Method(visibilityType, "IsWorldPositionExploredFor", new[] { typeof(int), typeof(int) }, null);
                if (s_IsExploredFor == null) failures.Add("VisibilityController.IsWorldPositionExploredFor");
            }
            catch (Exception e)
            {
                failures.Add("exception: " + e.Message);
            }

            ReadyStatic = failures.Count == 0;
            ResolveError = string.Join("; ", failures);
        }

        public bool IsSettlementSiteTile(int tileIndex)
        {
            try
            {
                var settlementDefinition = s_SettlementDefinitionField.GetValue(null);
                if (settlementDefinition == null) return false;
                return (bool)s_CheckTerrainAndPoi.Invoke(null, new object[] { settlementDefinition, tileIndex });
            }
            catch (Exception e)
            {
                Fail("CheckTerrainAndPOI: " + e.Message);
                return false;
            }
        }

        public object TryGetArmy(SimulationEntityGUID guid)
        {
            try
            {
                var repository = s_RepositoryField.GetValue(null);
                if (repository == null) return null;
                var args = new object[] { guid, null };
                var ok = (bool)s_TryGetArmy.Invoke(repository, args);
                return ok ? args[1] : null;
            }
            catch (Exception e)
            {
                Fail("TryGetSimulationEntity: " + e.Message);
                return null;
            }
        }

        public bool EvaluateTile(object army, int tileIndex, out DistrictPlacementEvaluation.ValidTile validTile, out FixedPoint score)
        {
            validTile = default(DistrictPlacementEvaluation.ValidTile);
            score = FixedPoint.Zero;
            if (army == null) return false;
            try
            {
                var evaluator = s_EvaluatorField.GetValue(null);
                var settlementDefinition = s_SettlementDefinitionField.GetValue(null);
                if (evaluator == null || settlementDefinition == null) return false;

                var vt = default(DistrictPlacementEvaluation.ValidTile);
                vt.Initialize(tileIndex);
                var args = new object[] { army, vt, tileIndex, settlementDefinition, null };
                s_EvaluateOne.Invoke(evaluator, args);
                vt = (DistrictPlacementEvaluation.ValidTile)args[1];

                var fims = vt.OverallFims;
                var ponderation = (FimsInfo)s_PonderationField.GetValue(evaluator);
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
        /// Fail-open on reflection issues. Exploration is monotonic, so a cached pick is only
        /// invalidated by VisibilityFrame when new scouting occurs during a settlement session.
        /// </summary>
        public bool IsTileExplored(int tileIndex, int empireIndex)
        {
            if (empireIndex < 0) return true;
            try
            {
                var visibilityController = s_VisibilityField.GetValue(null);
                if (visibilityController == null) return true;
                return (bool)s_IsExploredFor.Invoke(visibilityController, new object[] { tileIndex, empireIndex });
            }
            catch (Exception e)
            {
                Fail("IsWorldPositionExploredFor: " + e.Message);
                return true;
            }
        }

        public int GetArmyEmpireIndex(object army)
        {
            return army == null ? -1 : GetIntMember(army, "EmpireIndex");
        }

        public bool GetCurrentFrames(out int districtFrame, out int constructionFrame, out int visibilityFrame)
        {
            districtFrame = constructionFrame = visibilityFrame = 0;
            try
            {
                var world = s_WorldField.GetValue(null);
                var visibilityController = s_VisibilityField.GetValue(null);
                if (world == null || visibilityController == null) return false;

                var districtInfo = GetMemberValue(world, "DistrictInfo");
                var constructionInfo = GetMemberValue(world, "ConstructionInfo");
                if (districtInfo == null || constructionInfo == null) return false;

                districtFrame = GetIntMember(districtInfo, "Frame");
                constructionFrame = GetIntMember(constructionInfo, "Frame");
                visibilityFrame = GetIntMember(visibilityController, "Frame");
                return true;
            }
            catch (Exception e)
            {
                Fail("frame counters: " + e.Message);
                return false;
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

        public bool GetTerritoryFacts(int territoryIndex, out bool isOcean, out bool isClaimed, out int[] tiles)
        {
            isOcean = true;
            isClaimed = true;
            tiles = s_NoTiles;
            try
            {
                var world = AiSnapshots.World;
                var territories = world == null ? null : world.Territories;
                if (territories == null || territoryIndex < 0 || territoryIndex >= territories.Length) return false;
                var territory = territories[territoryIndex];
                isOcean = territory.IsOcean;
                isClaimed = territory.Info.Claimed;
                var siteTiles = territory.TileIndexes;
                if (siteTiles != null) tiles = siteTiles;
                return true;
            }
            catch (Exception e)
            {
                Fail("territory facts: " + e.Message);
                return false;
            }
        }

        public int[] LandAdjacentTerritories(int territoryIndex)
        {
            try
            {
                var world = AiSnapshots.World;
                var territories = world == null ? null : world.Territories;
                if (territories == null || territoryIndex < 0 || territoryIndex >= territories.Length) return s_NoTiles;
                return LandAdjacentIndexes(territories[territoryIndex].Adjacencies);
            }
            catch (Exception e)
            {
                Fail("territory adjacencies: " + e.Message);
                return s_NoTiles;
            }
        }

        /// <summary>
        /// Keeps only Open-transition edges, in snapshot order. Data in/indexes out so the
        /// range harness can drive the production filter directly.
        /// </summary>
        internal static int[] LandAdjacentIndexes(Territory.Adjacency[] adjacencies)
        {
            if (adjacencies == null) return s_NoTiles;
            var indexes = new int[adjacencies.Length];
            int count = 0;
            for (int i = 0; i < adjacencies.Length; i++)
            {
                if ((adjacencies[i].Transition & TerritoryTransition.Open) == TerritoryTransition.None) continue;
                indexes[count++] = adjacencies[i].TerritoryIndex;
            }
            if (count == indexes.Length) return indexes;
            var trimmed = new int[count];
            Array.Copy(indexes, trimmed, count);
            return trimmed;
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

        private static int GetIntMember(object target, string name)
        {
            var value = GetMemberValue(target, name);
            return value is int ? (int)value : 0;
        }

        private static object GetMemberValue(object target, string name)
        {
            var type = target.GetType();
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null) return field.GetValue(target);
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return property == null ? null : property.GetValue(target, null);
        }
    }
}
