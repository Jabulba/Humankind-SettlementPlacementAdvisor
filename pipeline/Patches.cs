using System;
using System.Reflection;
using Amplitude.Mercury.Data.Simulation;
using Amplitude.Mercury.Interop;
using Amplitude.Mercury.Simulation;
using HarmonyLib;
using SettlementPlacementAdvisor.composition;
using SettlementPlacementAdvisor.gameapi;
using SettlementPlacementAdvisor.logger;
using SettlementPlacementAdvisor.presentation;

// System map. Simulation thread: these patches -> Advisor -> PickComputation -> EvaluationWriter; the scan
// publishes to the presentation channel. Presentation thread: the FIMS-pin postfix below reads the published
// PresentationView, and the advised-tile overlay is vanilla's own, driven by the Showable entries the writer
// writes. The advisor draws no fx of its own.
//
// Takeover points: Harmony prefixes on SettlementPlacementCursorSnapshot.EvaluatePlacement (mode dispatch,
// activation baseline, scan publish; the vanilla body is skipped) and on
// CampPlacementEvaluation.UpdateCurrentPosition (the per-refresh scan, cursor slot write and publish; the
// vanilla body is skipped). Supporting postfixes: CampPlacementEvaluation.Initialize (activation tail
// reserve), SettlementPlacementCursor.DeactivateCursor (publish inactive), and
// SettlementPlacementPinsSubset.PinMustMeShown (the pin gate). The lifecycle prefix on
// SandboxManager.Start empties the pick cache for a new game: it runs on the game's start thread, with no
// pass in flight, before vanilla creates that game's simulation thread.
//
// Patches reach the game only through SpaRuntime's read-only hand-off and the game surface seam: never into
// the adapter's binding table, labels, or compiled delegates. Mode dispatch is the mode-policy table's alone.

namespace SettlementPlacementAdvisor.pipeline
{
	/// <summary>
	///     Snapshot sync takeover: mode dispatch on the snapshot's stamped SettleType, the activation baseline
	///     reserve, and a scan pass that publishes without writing. A wired mode skips vanilla's body.
	/// </summary>
	/// <remarks>
	///     <para>An unmapped mode fails closed: publish inactive, write nothing, hand vanilla's body back.</para>
	///     <para>
	///         The scanned army is the placement entity GUID: settlementGuid, the GUID vanilla resolves the
	///         placing Army from. It is never hoveredSettlementGuid, which names the settlement under the cursor.
	///     </para>
	/// </remarks>
	[HarmonyPatch]
	internal static class PlacementScanPatch
	{
		private static MethodBase TargetMethod()
		{
			return SpaRuntime.GameApi.PatchTarget(PatchPoint.EvaluatePlacement);
		}

		private static bool Prefix(
			SettlementPlacementCursorSnapshot __instance,
			SimulationEntityGUID settlementGuid,
			CampPlacementEvaluation placementEvaluation)
		{
			try
			{
				ArmyActionType mode = StampedMode(__instance, placementEvaluation);
				if (!ModePolicy.IsWired(mode))
				{
					PresentationView.PublishInactive(mode);
					return true;
				}

				SpaRuntime.Writer.ReserveBaseline(placementEvaluation);
				SpaRuntime.Advisor.RunScanPass(placementEvaluation, CursorTileOf(__instance), settlementGuid);
				return false;
			}
			catch (Exception e)
			{
				SpaLogger.Error("EvaluatePlacement takeover: " + e.Message);
				SpaRuntime.Advisor.PublishEmpty(placementEvaluation);
				return false;
			}
		}

		private static ArmyActionType StampedMode(
			SettlementPlacementCursorSnapshot snapshot,
			CampPlacementEvaluation evaluation)
		{
			if (!SpaRuntime.GameApi.TryReadSnapshotActionType(snapshot, out ArmyActionType mode))
			{
				SpaLogger.Error("snapshot member actionType");
				return ArmyActionType.AttackArmy;
			}

			evaluation.SettleType = mode;
			return mode;
		}

		private static int CursorTileOf(SettlementPlacementCursorSnapshot snapshot)
		{
			return SpaRuntime.GameApi.ReadSnapshotCursorTile(snapshot);
		}
	}

	/// <summary>
	///     Refresh takeover: the per-refresh scan, the cursor slot write and the publish; vanilla's body is
	///     skipped.
	/// </summary>
	/// <remarks>
	///     <para>
	///         A refresh on a mode the table has not wired hands vanilla's body back: publish inactive, write
	///         nothing, return true. Vanilla's own preview then runs untouched.
	///     </para>
	/// </remarks>
	[HarmonyPatch]
	internal static class SettlementEvaluationPatch
	{
		private static MethodBase TargetMethod()
		{
			return SpaRuntime.GameApi.PatchTarget(PatchPoint.UpdateCurrentPosition);
		}

		private static bool Prefix(
			CampPlacementEvaluation __instance,
			int currentPositionTileIndex,
			SimulationEntityGUID armyGUID)
		{
			if (!Advisor.OwnsRefresh(__instance))
			{
				PresentationView.PublishInactive(__instance
					.SettleType);
				return true;
			}

			try
			{
				SpaRuntime.Advisor.Run(__instance, currentPositionTileIndex, armyGUID);
				return false;
			}
			catch (Exception e)
			{
				SpaLogger.Error("UpdateCurrentPosition takeover: " + e.Message);
				SpaRuntime.Advisor.PublishEmpty(__instance);
				return false;
			}
		}
	}

	/// <summary>
	///     Activation tail reserve: the writer reserves the tail window at the activation's count and scrubs the
	///     tail the previous mode's session left.
	/// </summary>
	[HarmonyPatch]
	internal static class EvaluationActivationPatch
	{
		private static MethodBase TargetMethod()
		{
			return SpaRuntime.GameApi.PatchTarget(PatchPoint.EvaluationInitialize);
		}

		private static void Postfix(CampPlacementEvaluation __instance)
		{
			SpaRuntime.Writer.NoteActivation(__instance);
		}
	}

	/// <summary>
	///     Publish inactive on vanilla's own cursor deactivation: the overlay clears with vanilla's cursor.
	/// </summary>
	/// <remarks>
	///     <para>
	///         The cursor type is Unity-native-bound, so the headless suite asserts this patch's registration
	///         and invokes the postfix; firing it on a real deactivation is a named row of the manual in-game smoke
	///         checklist. The postfix takes no parameters, so no Harmony injection needs proving there.
	///     </para>
	/// </remarks>
	[HarmonyPatch]
	internal static class CursorDeactivatePatch
	{
		private static MethodBase TargetMethod()
		{
			return SpaRuntime.GameApi.PatchTarget(PatchPoint.DeactivateCursor);
		}

		private static void Postfix()
		{
			PresentationView.PublishInactive(PresentationView.Current.Mode);
		}
	}

	/// <summary>
	///     Pin gate on vanilla's ref result: membership in the published pick set, and vanilla's hovered-tile
	///     rule on the cursor tile; non-members are off. Vanilla's generic pin filters stay in force, so a
	///     zero-yield or off-screen member gets hidden.
	/// </summary>
	/// <remarks>
	///     <para>
	///         A mode the published view carries that the table has not wired leaves vanilla's answer alone.
	///         Presentation code reads only the captured view.
	///     </para>
	/// </remarks>
	[HarmonyPatch]
	internal static class SettlementPinsPatch
	{
		private static MethodBase TargetMethod()
		{
			return SpaRuntime.GameApi.PatchTarget(PatchPoint.PinMustMeShown);
		}

		private static void Postfix(ref bool __result, DistrictPlacementEvaluation.ValidTile info)
		{
			PresentationView view = PresentationView.Current;

			if (!ModePolicy.IsWired(view.Mode))
			{
				return;
			}

			if (!view.IsActive)
			{
				__result = false;
				return;
			}

			__result = view.CursorTile == info.TileIndex
						   ? HoveredRuleAllows(view.SettleFailure)
						   : view.Contains(info.TileIndex);
		}

		private static bool HoveredRuleAllows(ArmyActionFailureFlags settleFailure)
		{
			return HoveredRule.Allows(settleFailure);
		}
	}

	/// <summary>
	///     Sandbox start: the pick cache goes empty for the game about to run. Vanilla starts one sandbox at a
	///     time and refuses a start while a shutdown is pending, so this prefix runs with no scan pass in
	///     flight, before the new sandbox's simulation thread is created, and that thread start is what makes
	///     the cleared cache visible to it. Vanilla's body runs untouched.
	/// </summary>
	/// <remarks>
	///     <para>
	///         The headless suite proves the patch is bound to its declared owner and applied. Firing on a real
	///         start, and the pick set the new game's first hover produces afterwards, is a named row of the
	///         manual in-game smoke checklist.
	///     </para>
	/// </remarks>
	[HarmonyPatch]
	internal static class SandboxStartPatch
	{
		private static MethodBase TargetMethod()
		{
			return SpaRuntime.GameApi.PatchTarget(PatchPoint.SandboxStart);
		}

		private static void Prefix()
		{
			SpaRuntime.Advisor.ResetPicks();
		}
	}
}
