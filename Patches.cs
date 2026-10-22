using System;
using System.Reflection;
using Amplitude.Mercury.Interop;
using Amplitude.Mercury.Simulation;
using HarmonyLib;

namespace SettlementPlacementAdvisor
{
    /// <summary>
    /// Simulation thread: after vanilla's current-position update, run the advisor and publish
    /// its pick set. Failures only deactivate that set; they never write the evaluation.
    /// </summary>
    [HarmonyPatch]
    internal static class CampEvaluationPatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method("Amplitude.Mercury.Interop.CampPlacementEvaluation:UpdateCurrentPosition");
        }

        private static void Postfix(CampPlacementEvaluation __instance, int currentPositionTileIndex, SimulationEntityGUID armyGUID)
        {
            try
            {
                Plugin.Instance.Advisor.Run(__instance, currentPositionTileIndex, armyGUID);
            }
            catch (Exception e)
            {
                Plugin.Instance.Advisor.Deactivate(); // failure path never writes the evaluation
                GameApi.Fail("UpdateCurrentPosition postfix: " + e.Message);
            }
        }
    }

    /// <summary>
    /// Simulation thread: after Initialize, vanilla's valid positions are final. This is the
    /// only safe window to reserve the writer's tail before the cursor starts refreshing.
    /// Initialize is shared with city founding, which the writer ignores.
    /// </summary>
    [HarmonyPatch]
    internal static class EvaluationActivationPatch
    {
        private static MethodBase TargetMethod()
        {
            // Explicit parameter types: the base class declares Initialize() and
            // Initialize(GameScenarioDefinition, int), so a name-only lookup is ambiguous.
            return AccessTools.Method("Amplitude.Mercury.Interop.CampPlacementEvaluation:Initialize",
                new Type[] { typeof(CustomTileIndexArea) });
        }

        private static void Postfix(CampPlacementEvaluation __instance)
        {
            if (Plugin.Instance == null) return;
            Plugin.Instance.Writer.NoteActivation(__instance);
        }
    }

    /// <summary>
    /// Presentation thread: clear advisor stars on cursor deactivation (mode exit or cursor
    /// switch), mirroring vanilla's tile-feedback clear.
    /// </summary>
    [HarmonyPatch]
    internal static class CursorDeactivatePatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method("Amplitude.Mercury.Presentation.SettlementPlacementCursor:DeactivateCursor");
        }

        private static void Postfix()
        {
            Plugin.Instance.Stars.Clear();
        }
    }

    /// <summary>
    /// Presentation thread: FIMS-pin gate. Show pins on advisor picks and suppress vanilla
    /// top-2 pins outside the set. An inactive set (null, or Tiles == null) keeps vanilla's
    /// answer, so city placement and inactive refreshes are untouched. Membership is answered
    /// from the captured snapshot, so pins do not flip per mouse move.
    /// </summary>
    [HarmonyPatch]
    internal static class SettlementPinsPatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method("Amplitude.Mercury.UI.SettlementPlacementPinsSubset:PinMustMeShown");
        }

        private static void Postfix(ref bool __result, DistrictPlacementEvaluation.ValidTile info)
        {
            var set = PickSet.Current;
            if (set == null || set.Tiles == null) return;             // advisor inactive: vanilla gate
            if (set.Contains(info.TileIndex)) { __result = true; return; }

            var snapshot = Snapshots.SettlementPlacementCursorSnapshot;
            if (snapshot == null || !snapshot.HasBeenRefreshedSinceStarted) return;
            var best = snapshot.PresentationData.PlacementEvaluation.BestCampPlacementTileIndexes;
            if (best == null) return;
            for (int i = 0; i < best.Length; i++)
                if (best[i] == info.TileIndex) { __result = false; break; }
        }
    }
}
