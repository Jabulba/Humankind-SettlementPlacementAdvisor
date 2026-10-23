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
    /// unmapped type (SettlementPlacementCursorSnapshot.cs:67-69). An advisor failure in a wired
    /// mode never falls back to vanilla's evaluation: the tail is cleared and the run publishes
    /// INACTIVE.
    /// </summary>
    [HarmonyPatch]
    internal static class PlacementScanPatch
    {
        private static MethodBase TargetMethod()
        {
            return GameApi.BoundTarget(GameApi.PatchEvaluatePlacement);
        }

        private static bool Prefix(SettlementPlacementCursorSnapshot __instance, SimulationEntityGUID settlementGuid, CampPlacementEvaluation placementEvaluation)
        {
            var plugin = Plugin.Instance;
            if (plugin == null) return true; // advisor not composed: vanilla untouched

            var mode = StampedMode(__instance, placementEvaluation);
            if (!ModePolicy.IsWired(mode))
            {
                PresentationView.PublishInactive(mode); // INACTIVE: nothing written, vanilla decides this mode
                return true;
            }

            try
            {
                // Vanilla's full settlement evaluation was the only Initialize caller in a wired
                // mode (SimulationEvaluator.cs:2332, the settlement and city founding branches), and
                // the takeover replaces it, so the advisor runs vanilla's activation here: the
                // baseline, the tail window, and the retired best-camp arrays all come from that call
                // (SPEC section 1 row 5).
                plugin.Writer.ReserveBaseline(placementEvaluation);
                plugin.Advisor.RunScanPass(placementEvaluation, CursorTileOf(__instance), settlementGuid);
                return false; // vanilla's full settlement evaluation is never called from this snapshot
            }
            catch (Exception e)
            {
                GameApi.Fail("EvaluatePlacement takeover: " + e.Message);
                plugin.Advisor.PublishEmpty(placementEvaluation); // INACTIVE, tail cleared, no vanilla fallback
                return false;
            }
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
            var stamped = GameApi.BoundMemberValue(snapshot, GameApi.SnapshotActionType);
            if (!(stamped is ArmyActionType))
            {
                GameApi.Fail("snapshot member actionType");
                return ArmyActionType.AttackArmy; // unwired: fail closed
            }
            var mode = (ArmyActionType)stamped;
            evaluation.SettleType = mode;
            return mode;
        }

        /// <summary>
        /// The cursor tile the scan pass centers on: the position the snapshot is synchronizing
        /// (ConstructiblePlacementCursorSnapshot.nextConstructiblePosition, read under the
        /// snapshot mutex at :80), a startup-bound member. -1 when the position is invalid.
        /// </summary>
        private static int CursorTileOf(SettlementPlacementCursorSnapshot snapshot)
        {
            var boxed = GameApi.BoundMemberValue(snapshot, GameApi.SnapshotCursorPosition);
            if (!(boxed is WorldPosition)) return -1;
            var position = (WorldPosition)boxed;
            return position.IsWorldPositionValid() ? position.ToTileIndex() : -1;
        }
    }

    /// <summary>
    /// Simulation thread, per cursor refresh: the advisor's takeover of the hover refresh. In a mode
    /// it has taken over the advisor writes the cursor slot (the TailStart entry) itself from the
    /// mode gate call with no mask, publishes the admission set its pipeline computed, and vanilla's
    /// whole refresh body is skipped - its append-one-tile hover rule and resize-by-10 die with it,
    /// so the cursor slot is written once, by the advisor. A mode the mode-policy table has not
    /// wired, and a surface that cannot answer, leave vanilla running its own refresh: its own
    /// cursor entry survives, and the advisor writes nothing. An advisor failure in a wired mode
    /// clears the advisor's tail and publishes INACTIVE; it never re-runs vanilla's refresh body.
    /// </summary>
    [HarmonyPatch]
    internal static class SettlementEvaluationPatch
    {
        private static MethodBase TargetMethod()
        {
            return GameApi.BoundTarget(GameApi.PatchUpdateCurrentPosition);
        }

        private static bool Prefix(CampPlacementEvaluation __instance, int currentPositionTileIndex, SimulationEntityGUID armyGUID)
        {
            var plugin = Plugin.Instance;
            if (plugin == null) return true; // advisor not composed: vanilla refreshes untouched

            if (!plugin.Advisor.OwnsRefresh(__instance))
            {
                PresentationView.PublishInactive(__instance.SettleType); // INACTIVE: vanilla's refresh keeps its own cursor entry
                return true;
            }

            try
            {
                plugin.Advisor.Run(__instance, currentPositionTileIndex, armyGUID);
                return false;
            }
            catch (Exception e)
            {
                GameApi.Fail("UpdateCurrentPosition takeover: " + e.Message);
                plugin.Advisor.PublishEmpty(__instance); // the advisor owns this refresh: INACTIVE, tail cleared
                return false;
            }
        }
    }

    /// <summary>
    /// Simulation thread: after Initialize, vanilla's valid positions are final. This is the only
    /// safe window to release the tail a previous session left and reserve the writer's new window
    /// before the cursor starts refreshing. Initialize is shared by every placement mode, and every
    /// mode's activation runs through here.
    /// </summary>
    [HarmonyPatch]
    internal static class EvaluationActivationPatch
    {
        private static MethodBase TargetMethod()
        {
            // The startup probe resolved this overload once, with the game's own parameter shape
            // (the base class also declares Initialize()): the bound table holds the target.
            return GameApi.BoundTarget(GameApi.EvaluationInitialize);
        }

        private static void Postfix(CampPlacementEvaluation __instance)
        {
            if (Plugin.Instance == null) return;
            Plugin.Instance.Writer.NoteActivation(__instance);
        }
    }

    /// <summary>
    /// Presentation thread: clear advisor stars on cursor deactivation (mode exit or cursor
    /// switch), mirroring vanilla's tile-feedback clear. The presentation channel follows the
    /// cursor out of the mode: the published set belongs to the mode the cursor was in, so leaving
    /// it takes the channel inactive for that mode. A stale published set from an exited mode must
    /// never gate pins.
    /// </summary>
    [HarmonyPatch]
    internal static class CursorDeactivatePatch
    {
        private static MethodBase TargetMethod()
        {
            return GameApi.BoundTarget(GameApi.PatchDeactivateCursor);
        }

        private static void Postfix()
        {
            var plugin = Plugin.Instance;
            if (plugin == null) return;
            plugin.Stars.Clear();
            PresentationView.PublishInactive(PresentationView.Current.Mode);
        }
    }

    /// <summary>
    /// Presentation thread: the FIMS-pin gate, the shown set's second rendering. A published member
    /// shows its pin when the channel's cursor-slot failure allows it, everything else is off, and
    /// an inactive channel shows no pin at all: no vanilla best-placement surface takes the
    /// advisor's place. The gate answers from one captured channel read, so pins do not flip per
    /// mouse move, and every fact it uses - membership, the hovered-tile rule - is a published
    /// fact, never a live game field. A placement mode the advisor never published for keeps
    /// vanilla's gate answer. Vanilla's generic pin filters (tile index, exploration, zero gains and
    /// losses, on-screen culling) run above this hook in the unpatched chain, so a zero-yield or
    /// off-screen member gets its star and no pin. The retired best-camp array is never read.
    /// </summary>
    [HarmonyPatch]
    internal static class SettlementPinsPatch
    {
        private static MethodBase TargetMethod()
        {
            return GameApi.BoundTarget(GameApi.PatchPinMustMeShown);
        }

        private static void Postfix(ref bool __result, DistrictPlacementEvaluation.ValidTile info)
        {
            var view = PresentationView.Current; // captured once per gate pass
            if (!ModePolicy.IsWired(view.Mode))
                return; // a mode the advisor never published for: vanilla's gate, vanilla's surface
            if (!view.IsActive)
            {
                __result = false; // INACTIVE displays nothing
                return;
            }
            __result = view.Contains(info.TileIndex) && HoveredRuleAllows(view.SettleFailure);
        }

        /// <summary>
        /// Vanilla's hovered-tile rule (SettlementPlacementPinsSubset.cs:29-33), read off the
        /// published cursor-slot failure: pins are admitted unless that answer reports a failure
        /// other than NotEnoughInfluence.
        /// </summary>
        private static bool HoveredRuleAllows(ArmyActionFailureFlags settleFailure)
        {
            return settleFailure == ArmyActionFailureFlags.None
                || settleFailure == ArmyActionFailureFlags_Part1.NotEnoughInfluence;
        }
    }
}
