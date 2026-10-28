using System;
using System.Collections.Generic;
using System.Reflection;
using Amplitude.Mercury.Data.Simulation;
using Amplitude.Mercury.Interop;
using Amplitude.Mercury.Simulation;
using HarmonyLib;
using SettlementPlacementAdvisor.composition;
using SettlementPlacementAdvisor.gameapi;
using SettlementPlacementAdvisor.pipeline;
using SettlementPlacementAdvisor.presentation;
using Xunit;

namespace SettlementPlacementAdvisor.Tests
{
	/// <summary>
	///     T2: Harmony takeover. The mod's patch classes are applied to the real game types with upstream Harmony;
	///     the patched game methods run for real, the prefixes skip vanilla, and the advisor graph (SpaRuntime)
	///     runs on the scripted surface.
	/// </summary>
	/// <remarks>
	///     <para>
	///         Patch methods the game cannot host headless (the activation postfix, the pin postfix, the cursor
	///         deactivation, the scan prefix) are invoked by reflection, with the patch registration asserted against
	///         GetPatchedMethods. A Harmony version mismatch warns here and the suite continues: the shipped net472
	///         fork cannot initialize under coreclr, so the suite binds NuGet Lib.Harmony 2.4.2.
	///     </para>
	///     <para>
	///         Headless ceiling: no SettlementPlacementCursor, World or Army construction (ECall, Unity and
	///         internal constructors), and no DistrictDefinition instance (abstract). What that leaves unproven rests on
	///         the decompiled anchors RESOURCES.md names, not on a test: the game surface's own fail-open/fail-closed
	///         polarity on the water, exploration, terrain, POI and resource lookups; the FimsTier vanilla's scoring
	///         leaves on an evaluated row, which is what makes a Showable row render as a white hex; and vanilla's
	///         consumption of the tail's ConditionalFlagsTileIndexes rows.
	///     </para>
	///     <para>
	///         The cursor's deactivation is the one takeover point whose body the suite never runs, so firing
	///         that postfix on a real deactivation is a named row of the manual in-game smoke checklist run with
	///         scripts/launch-game.sh, beside the banner check. It takes no parameters, so no Harmony injection needs
	///         proving there; the injection that matters is the pin gate's, on vanilla's ref parameter, and the
	///         registration assertion covers it.
	///     </para>
	/// </remarks>
	public class LifecycleTests : IDisposable
	{
		private static readonly SimulationEntityGUID Army = new SimulationEntityGUID(1UL);

		private readonly Harmony _Harmony = new Harmony("spatests");
		private readonly ScriptSurface _Surface = ScriptSurface.Standard();

		public LifecycleTests()
		{
			Probe.Start();
			Version harmony = Probe.HarmonyVersion;
			if (harmony != new Version(2, 4, 2, 0))
			{
				Console.WriteLine("WARNING: T2 patching runs on 0Harmony "
								  + harmony
								  + "; NuGet Lib.Harmony 2.4.2 is the pinned patching library. Check the 0Harmony"
								  + " conflict resolution in tests/Tests.csproj if an assembly shipped by the game"
								  + " won asset selection.");
			}

			_Surface.CursorTile = 101;
			SpaRuntime.GameApi = _Surface;
			SpaRuntime.Writer = new EvaluationWriter(_Surface);
			SpaRuntime.Advisor = new Advisor(_Surface, new PickComputation(_Surface), SpaRuntime.Writer);
			_Harmony.PatchAll(typeof(PlacementScanPatch).Assembly);
		}

		public void Dispose()
		{
			Probe.Unpatch(_Harmony, "spatests");
			Probe.ResetRuntime();
			Probe.ResetView();
		}

		[Fact]
		public void AllSixTakeoverPointsBindRealGameMethods()
		{
			HashSet<string> patched = new HashSet<string>();
			foreach (MethodBase method in _Harmony.GetPatchedMethods())
			{
				patched.Add(method.DeclaringType.FullName + "::" + method.Name);
			}

			Assert.Contains("Amplitude.Mercury.Interop.SettlementPlacementCursorSnapshot::EvaluatePlacement", patched);
			Assert.Contains("Amplitude.Mercury.Interop.CampPlacementEvaluation::UpdateCurrentPosition", patched);
			Assert.Contains("Amplitude.Mercury.Interop.CampPlacementEvaluation::Initialize", patched);
			Assert.Contains("Amplitude.Mercury.Presentation.SettlementPlacementCursor::DeactivateCursor", patched);
			Assert.Contains("Amplitude.Mercury.UI.SettlementPlacementPinsSubset::PinMustMeShown", patched);
			Assert.Contains("Amplitude.Mercury.Sandbox.SandboxManager::Start", patched);
		}

		[Fact]
		public void RefreshOnTheRealMethodWritesTheTailAndPublishes()
		{
			CampPlacementEvaluation evaluation = Probe.NewEvaluation(4, ArmyActionType.CreateCamp);

			Probe.InvokePatched(Probe.NewGameApi(), PatchPoint.UpdateCurrentPosition, evaluation,
				101, null, 0, Army);

			Assert.Equal(8, evaluation.ValidTileCount);
			Assert.Equal(4, evaluation.CurrentPositionIndex);
			Assert.Equal(101, evaluation.ValidTiles[4].TileIndex);
			Assert.Equal(new[] { 102, 103, 203 },
				new[]
				{
					evaluation.ValidTiles[5].TileIndex, evaluation.ValidTiles[6].TileIndex,
					evaluation.ValidTiles[7].TileIndex
				});
			for (int i = 4; i <= 7; i++)
			{
				Assert.True(evaluation.ValidTiles[i].Showable);
			}

			for (int i = 0; i < 4; i++)
			{
				Assert.Equal(5000 + i, evaluation.ValidTiles[i].TileIndex);
				Assert.False(evaluation.ValidTiles[i].Showable);
			}

			Assert.True(PresentationView.Current.IsActive);
			Assert.True(PresentationView.Current.Contains(203));
			Assert.Equal(101, PresentationView.Current.CursorTile);
		}

		[Fact]
		public void ActivationPostfixReservesTheBaselineOnTheRealEvaluation()
		{
			CampPlacementEvaluation evaluation = Probe.NewEvaluation(4, ArmyActionType.CreateCamp);

			Probe.InvokePatch(typeof(EvaluationActivationPatch), "Postfix", evaluation);

			Assert.Equal(4, evaluation.ValidTileCount);
			Assert.Equal(4 + 1 + 90, evaluation.ValidTiles.Length);
			Assert.Equal(-1, evaluation.ValidTiles[4].TileIndex);
		}

		[Fact]
		public void TakeoverStampsSkipsVanillaAndPublishesTheScan()
		{
			SettlementPlacementCursorSnapshot snapshot = Probe.NewSnapshot();
			CampPlacementEvaluation evaluation = Probe.NewEvaluation(4, ArmyActionType.CreateCamp);

			Probe.InvokePatched(Probe.NewGameApi(), PatchPoint.EvaluatePlacement, snapshot,
				null, 0, Army, _Surface.HoveredSettlementGuid, evaluation);

			Assert.Equal(ArmyActionType.CreateCamp, evaluation.SettleType);
			// The scan pass reserves the baseline and publishes; it writes no pick rows.
			Assert.Equal(4, evaluation.ValidTileCount);
			Assert.Equal(4 + 1 + 90, evaluation.ValidTiles.Length);
			Assert.True(PresentationView.Current.IsActive);
			Assert.True(PresentationView.Current.Contains(102));
			Assert.Equal(PresentationView.NoCursorTile, PresentationView.Current.CursorTile);
		}

		[Fact]
		public void PipelineFailurePublishesEmptyFromThePrefix()
		{
			_Surface.ThrowArmyGate = true;
			CampPlacementEvaluation evaluation = Probe.NewEvaluation(4, ArmyActionType.CreateCamp);

			Probe.InvokePatched(Probe.NewGameApi(), PatchPoint.UpdateCurrentPosition, evaluation,
				101, null, 0, Army);

			Assert.False(PresentationView.Current.IsActive);
			Assert.Equal(-1, evaluation.CurrentPositionIndex);
			Assert.Equal(-1, evaluation.ValidTiles[4].TileIndex);
			Assert.Contains(Probe.LogLines, line => line.Contains("UpdateCurrentPosition takeover"));
		}

		[Fact]
		public void PinGateFollowsMembershipAndTheHoveredRule()
		{
			PresentationView.Publish(new[] { 101, 102 }, ArmyActionType.CreateCamp, ArmyActionFailureFlags.None, 102);
			Assert.True(Gate(101, true));
			Assert.False(Gate(999, true));
			Assert.True(Gate(102, false));

			PresentationView.Publish(new[] { 101, 102 }, ArmyActionType.CreateCamp,
				ArmyActionFailureFlags_Part1.NotEnoughMoney, 102);
			Assert.False(Gate(102, true));

			PresentationView.Publish(new[] { 101, 102 }, ArmyActionType.CreateCamp,
				ArmyActionFailureFlags_Part1.NotEnoughInfluence, 102);
			Assert.True(Gate(102, false));

			PresentationView.PublishInactive(ArmyActionType.CreateCamp);
			Assert.False(Gate(101, true));

			// An unwired mode's view leaves the vanilla answer alone.
			PresentationView.Publish(new[] { 101 }, ArmyActionType.Ransack, ArmyActionFailureFlags.None, -1);
			Assert.True(Gate(101, true));
			Assert.True(Gate(999, true));
		}

		[Fact]
		public void CursorDeactivationPublishesInactive()
		{
			PresentationView.Publish(new[] { 102 }, ArmyActionType.CreateCamp, ArmyActionFailureFlags.None, -1);
			Assert.True(PresentationView.Current.IsActive);

			Probe.InvokePatch(typeof(CursorDeactivatePatch), "Postfix");

			Assert.False(PresentationView.Current.IsActive);
		}

		[Fact]
		public void SandboxStartPrefixEmptiesThePickCache()
		{
			SettlementPlacementCursorSnapshot snapshot = Probe.NewSnapshot();
			CampPlacementEvaluation evaluation = Probe.NewEvaluation(4, ArmyActionType.CreateCamp);
			object[] args = { snapshot, Army, evaluation };

			Probe.InvokePatch(typeof(PlacementScanPatch), "Prefix", args);
			int scanned = _Surface.EvaluateTileCalls;

			// The same pass again serves from the cache: the scan graph runs and no tile is scored.
			Probe.InvokePatch(typeof(PlacementScanPatch), "Prefix", args);
			Assert.Equal(scanned, _Surface.EvaluateTileCalls);

			// The new game's start: the patch resets the cache, so the next pass scores the whole
			// neighborhood again.
			Probe.InvokePatch(typeof(SandboxStartPatch), "Prefix");
			Probe.InvokePatch(typeof(PlacementScanPatch), "Prefix", args);
			Assert.Equal(2 * scanned, _Surface.EvaluateTileCalls);
		}

		[Fact]
		public void ZeroPicksPublishesInactiveOnTheRealRefresh()
		{
			_Surface.Territories[0].Claimed = true;
			_Surface.Territories[1].Claimed = true;
			CampPlacementEvaluation evaluation = Probe.NewEvaluation(4, ArmyActionType.CreateCamp);

			Probe.InvokePatched(Probe.NewGameApi(), PatchPoint.UpdateCurrentPosition, evaluation,
				101, null, 0, Army);

			Assert.False(PresentationView.Current.IsActive);
			Assert.Equal(-1, evaluation.CurrentPositionIndex);
			Assert.Equal(-1, evaluation.ValidTiles[4].TileIndex);
		}

		[Fact]
		public void UnmappedModeHandsVanillaBackAfterPublishingInactive()
		{
			_Surface.TryReadModeAnswer = false;
			SettlementPlacementCursorSnapshot snapshot = Probe.NewSnapshot();
			CampPlacementEvaluation evaluation = Probe.NewEvaluation(4, ArmyActionType.CreateCamp);

			object[] args = { snapshot, Army, evaluation };
			bool vanillaRuns = (bool)Probe.InvokePatch(typeof(PlacementScanPatch), "Prefix", args);

			Assert.True(vanillaRuns);
			Assert.Equal(ArmyActionType.AttackArmy, PresentationView.Current.Mode);
			Assert.Equal(ArmyActionType.CreateCamp, evaluation.SettleType);
			Assert.False(PresentationView.Current.IsActive);
			Assert.Equal(4, evaluation.ValidTileCount);
		}

		[Fact]
		public void ScanTakesTheArmyFromThePlacementEntityGuidNotTheHoveredSettlementGuid()
		{
			SettlementPlacementCursorSnapshot snapshot = Probe.NewSnapshot();

			// The scripted surface answers for the army GUID and for no other. In the right slots
			// the scan publishes; with the two GUID parameters swapped it has no army to scan for.
			CampPlacementEvaluation placed = Probe.NewEvaluation(4, ArmyActionType.CreateCamp);
			Probe.InvokePatched(Probe.NewGameApi(), PatchPoint.EvaluatePlacement, snapshot,
				null, 0, Army, _Surface.HoveredSettlementGuid, placed);
			Assert.True(PresentationView.Current.Contains(102));

			CampPlacementEvaluation swapped = Probe.NewEvaluation(4, ArmyActionType.CreateCamp);
			Probe.InvokePatched(Probe.NewGameApi(), PatchPoint.EvaluatePlacement, snapshot,
				null, 0, _Surface.HoveredSettlementGuid, Army, swapped);

			Assert.False(PresentationView.Current.IsActive);
			Assert.Equal(4, swapped.ValidTileCount);
			Assert.Equal(-1, swapped.CurrentPositionIndex);
		}

		[Fact]
		public void ScanTakeoverFailurePublishesEmptyFromThePrefix()
		{
			_Surface.ThrowArmyGate = true;
			SettlementPlacementCursorSnapshot snapshot = Probe.NewSnapshot();
			CampPlacementEvaluation evaluation = Probe.NewEvaluation(4, ArmyActionType.CreateCamp);

			Probe.InvokePatched(Probe.NewGameApi(), PatchPoint.EvaluatePlacement, snapshot,
				null, 0, Army, _Surface.HoveredSettlementGuid, evaluation);

			// The scan prefix catches, publishes empty and still skips vanilla. Publishing empty
			// writes the scrubbed cursor slot, so the count the prefix reserved is raised by it.
			Assert.False(PresentationView.Current.IsActive);
			Assert.Equal(5, evaluation.ValidTileCount);
			Assert.Equal(-1, evaluation.CurrentPositionIndex);
			Assert.Equal(-1, evaluation.ValidTiles[4].TileIndex);
			Assert.Contains(Probe.LogLines, line => line.Contains("EvaluatePlacement takeover"));
		}

		[Fact]
		public void UnwiredModeRefreshHandsVanillaBackAfterPublishingInactive()
		{
			CampPlacementEvaluation evaluation = Probe.NewEvaluation(4, ArmyActionType.Ransack);
			object[] args = { evaluation, 101, Army };

			bool vanillaRuns = (bool)Probe.InvokePatch(typeof(SettlementEvaluationPatch), "Prefix", args);

			Assert.True(vanillaRuns);
			Assert.False(PresentationView.Current.IsActive);
			Assert.Equal(ArmyActionType.Ransack, PresentationView.Current.Mode);
			Assert.Equal(4, evaluation.ValidTileCount);
			Assert.Equal(-1, evaluation.CurrentPositionIndex);
		}

		private static bool Gate(int tile, bool vanilla)
		{
			DistrictPlacementEvaluation.ValidTile entry = default;
			entry.Initialize(tile);
			object[] args = { vanilla, entry };
			Probe.InvokePatch(typeof(SettlementPinsPatch), "Postfix", args);
			return (bool)args[0];
		}
	}
}
