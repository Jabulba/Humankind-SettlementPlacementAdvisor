using System;
using System.Reflection;
using Amplitude.Mercury; // WorldPosition (the cursor position the scan pass reads)
using Amplitude.Mercury.Data.Simulation; // ArmyActionType
using Amplitude.Mercury.Interop;
using Amplitude.Mercury.Simulation;
using HarmonyLib;

namespace SettlementPlacementAdvisor
{
    /// <summary>
    /// Simulation thread, snapshot sync: the takeover point. The advisor's own pipeline replaces
    /// vanilla's full settlement evaluation (SimulationEvaluator.cs:2328), which this snapshot
    /// never calls. Mode dispatch on the stamped SettleType: a mode the mode-policy table wires
    /// runs the advisor's scan pass and vanilla's body is skipped; any other mode publishes
    /// INACTIVE and leaves vanilla fully in charge, including vanilla's own error log for an
    /// unmapped type (SettlementPlacementCursorSnapshot.cs:67-69).
    /// </summary>
    [HarmonyPatch]
    internal static class PlacementScanPatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method("Amplitude.Mercury.Interop.SettlementPlacementCursorSnapshot:EvaluatePlacement");
        }

        private static bool Prefix(SettlementPlacementCursorSnapshot __instance, SimulationEntityGUID settlementGuid, CampPlacementEvaluation placementEvaluation)
        {
            var plugin = Plugin.Instance;
            if (plugin == null) return true; // advisor not composed: vanilla untouched

            if (!ModePolicy.IsWired(StampedMode(__instance, placementEvaluation)))
            {
                plugin.Advisor.Deactivate(); // INACTIVE: nothing written, vanilla decides this mode
                return true;
            }

            try
            {
                // Vanilla's full settlement evaluation was the only Initialize caller in camp
                // mode (SimulationEvaluator.cs:2332), and the takeover replaces it, so the advisor
                // runs vanilla's activation here: the baseline, the tail window, and the retired
                // best-camp arrays all come from that call (SPEC section 1 row 5).
                plugin.Writer.ReserveBaseline(placementEvaluation);
                plugin.Advisor.RunScanPass(placementEvaluation, CursorTileOf(__instance), settlementGuid);
            }
            catch (Exception e)
            {
                plugin.Advisor.Deactivate(); // failure path: INACTIVE, no writes, no vanilla fallback
                GameApi.Fail("EvaluatePlacement takeover: " + e.Message);
            }
            return false; // vanilla's full camp evaluation is never called from this snapshot
        }

        /// <summary>
        /// The stamped placement mode. Vanilla assigns SettleType from the snapshot's action type
        /// inside the body the takeover replaces (SettlementPlacementCursorSnapshot.cs:43), so the
        /// advisor stamps it here: the mode reaches the mode-policy dispatch, and the pin label
        /// keeps a coherent SettleType/BuildTime pair (PlacementPin.cs:201). A missing member
        /// fails closed to an unwired type, which leaves vanilla in charge.
        /// </summary>
        private static ArmyActionType StampedMode(SettlementPlacementCursorSnapshot snapshot, CampPlacementEvaluation evaluation)
        {
            var field = typeof(SettlementPlacementCursorSnapshot).GetField("actionType",
                BindingFlags.NonPublic | BindingFlags.Instance); // plain reflection: the harnesses run no patcher
            if (field == null)
            {
                GameApi.Fail("snapshot member actionType");
                return ArmyActionType.AttackArmy; // unwired: fail closed
            }
            var mode = (ArmyActionType)field.GetValue(snapshot);
            evaluation.SettleType = mode;
            return mode;
        }

        /// <summary>
        /// The cursor tile the scan pass centers on: the position the snapshot is synchronizing
        /// (ConstructiblePlacementCursorSnapshot.nextConstructiblePosition, read under the
        /// snapshot mutex at :80). -1 when the member is gone or the position is invalid.
        /// </summary>
        private static int CursorTileOf(SettlementPlacementCursorSnapshot snapshot)
        {
            for (var type = snapshot.GetType(); type != null; type = type.BaseType)
            {
                var field = type.GetField("nextConstructiblePosition",
                    BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (field == null) continue;
                if (!(field.GetValue(snapshot) is WorldPosition)) return -1;
                var position = (WorldPosition)field.GetValue(snapshot);
                return position.IsWorldPositionValid() ? position.ToTileIndex() : -1;
            }
            GameApi.Fail("snapshot member nextConstructiblePosition");
            return -1;
        }
    }

    /// <summary>
    /// Simulation thread, per cursor refresh: vanilla keeps running the refresh, and the advisor
    /// publishes the admission set its pipeline computed for the cursor territory, writing it into
    /// the evaluation tail. Failures publish INACTIVE and never write the evaluation.
    /// </summary>
    [HarmonyPatch]
    internal static class SettlementEvaluationPatch
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
    /// Presentation thread: FIMS-pin gate. Pins follow the published pick set: members on,
    /// everything else off, answered from the captured set so pins do not flip per mouse move.
    /// An inactive set (null, or Tiles == null) hands the gate back to vanilla. Vanilla's generic
    /// pin filters (tile index, exploration, zero yields, on-screen culling) stay unoverridden.
    /// The best-camp placement array is never read: it retires with vanilla's best-of-2 loop.
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
            if (set == null || set.Tiles == null) return; // advisor inactive: vanilla gate
            __result = set.Contains(info.TileIndex);
        }
    }
}
