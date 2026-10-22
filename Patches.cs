using System;
using System.Reflection;
using Amplitude.Mercury.Interop;
using Amplitude.Mercury.Simulation;
using HarmonyLib;

namespace SettlementPlacementAdvisor
{
    /// <summary>
    /// Patch 1 (simulation thread): after the vanilla placement evaluation and current-position
    /// update, compute the advisor picks, hand them to the evaluation writer, and publish the
    /// pick set. The evaluation writer owns the ValidTiles tail and the vanilla glyph
    /// suppression, and the advisor keeps both strictly inside camp placement: on a non-camp
    /// evaluation, and on any failure, the postfix only deactivates our own pick set.
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
    /// Patch 4 (simulation thread): the camp placement evaluation finished its activation
    /// (Initialize), so the vanilla valid positions are final. The evaluation writer records
    /// the baseline count and reserves the tail capacity exactly once, in the only window
    /// where growing the array is safe (before the cursor starts refreshing). Initialize is
    /// shared with FoundCity/FoundCityWithSettler, so the writer guards the reservation to
    /// camp activations: a city placement gets no resize and no tail.
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
    /// Patch 3 (presentation thread): clear advisor stars when the camp placement cursor
    /// deactivates (mode exit / cursor switch), mirroring the vanilla tileFeedbacks.Clear().
    /// The star renderer stops every fx it tracks and forgets the rendered version.
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
    /// Patch 2 (presentation thread): membership-only FIMS-pin gate. Pins show on every
    /// advisor pick in the published pick set; vanilla top-2 pins outside the set are
    /// suppressed. An INACTIVE pick set hands the gate back to vanilla: both representations
    /// count (null instance, and the instance the advisor publishes when it deactivates,
    /// Tiles == null), so city placement and every advisor-inactive refresh keep the vanilla
    /// pin answer. Membership queries the captured snapshot instance, so the gate result is
    /// stable while the pick set content is stable (no per-mouse-move show/hide flips). The
    /// hovered cursor tile keeps its vanilla pin behavior.
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
