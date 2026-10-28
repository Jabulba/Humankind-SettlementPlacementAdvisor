using System.Reflection;
using SettlementPlacementAdvisor.gameapi;
using Xunit;

namespace SettlementPlacementAdvisor.Tests
{
	/// <summary>
	///     T3: the startup binding against the real restored game assemblies. This is the drift gate: a pin bump
	///     that breaks a binding fails these rows before a release ships, naming the unresolved binding. Drift
	///     detection is procedural.
	/// </summary>
	/// <remarks>
	///     <para>Only an unresolved binding fails here: a version difference alone is never a failure.</para>
	///     <para>
	///         GameApi.GameBuild reads the game's version statics, which are unset headless, so pin equality is
	///         asserted by the in-game load-failure line, not here.
	///     </para>
	/// </remarks>
	public class BindingTests
	{
		private static readonly string[] HotLabels =
		{
			ModePolicy.CampTileGateLabel, ModePolicy.FoundCityTileGateLabel, ModePolicy.SettlerTileGateLabel,
			GameApi.TerrainAndPoi, GameApi.Explored, GameApi.EvaluateOne, GameApi.PonderationInit,
			GameApi.TileToPosition
		};

		[Fact]
		public void BindingPassCompletesAgainstTheRealAssemblies()
		{
			Probe.Start();
			GameApi api = Probe.NewGameApi();

			Assert.NotNull(api);
			Assert.Equal("unknown", GameApi.GameBuild);
		}

		[Fact]
		public void EveryPatchPointResolvesOnItsDeclaredOwner()
		{
			Probe.Start();
			GameApi api = Probe.NewGameApi();

			AssertOwner(api, PatchPoint.EvaluatePlacement,
				"Amplitude.Mercury.Interop.SettlementPlacementCursorSnapshot");
			AssertOwner(api, PatchPoint.UpdateCurrentPosition, "Amplitude.Mercury.Interop.CampPlacementEvaluation");
			AssertOwner(api, PatchPoint.EvaluationInitialize, "Amplitude.Mercury.Interop.CampPlacementEvaluation");
			AssertOwner(api, PatchPoint.DeactivateCursor, "Amplitude.Mercury.Presentation.SettlementPlacementCursor");
			AssertOwner(api, PatchPoint.PinMustMeShown, "Amplitude.Mercury.UI.SettlementPlacementPinsSubset");
			AssertOwner(api, PatchPoint.SandboxStart, "Amplitude.Mercury.Sandbox.SandboxManager");
		}

		[Fact]
		public void TerritoryFrameReadResolvesTheFrameMemberOnTheConstructedGeneric()
		{
			Probe.Start();
			GameApi api = Probe.NewGameApi();

			// World's constructor needs Unity assets; the frame read only touches fields, so an
			// uninitialized World with a stamped TerritoryInfo frame exercises the compiled delegate
			// on the real field layout.
			Probe.InjectWorldTerritoryFrame(7);
			try
			{
				Assert.True(api.GetTerritoryFrame(out int territoryFrame));
				Assert.Equal(7, territoryFrame);
			}
			finally
			{
				Probe.ClearWorldInjection();
			}

			Assert.False(api.GetTerritoryFrame(out _));
		}

		[Fact]
		public void AStoppingBindingPassNamesTheRowItStoppedOn()
		{
			Probe.Start();

			// The drift a stopping pass carries is the row label Plugin.Awake prints beside the
			// supported and running game builds: one unresolved row, named, never a list.
			GameBuildDriftException drift = new GameBuildDriftException(GameApi.EvaluateOne);

			Assert.Equal(GameApi.EvaluateOne, drift.Binding);
			Assert.Contains(GameApi.EvaluateOne, drift.Message);
		}

		[Fact]
		public void AConstructedSurfaceHasEveryHotMemberCompiled()
		{
			Probe.Start();
			GameApi api = Probe.NewGameApi();

			// The pass returned, so every hot member is bound: nothing at runtime re-checks a
			// binding, and the hot labels the scan calls through must all be present.
			foreach (string label in HotLabels)
			{
				Assert.True(api.IsHotMemberBound(label), "unbound hot member: " + label);
			}
		}

		private static void AssertOwner(GameApi api, PatchPoint point, string owner)
		{
			MethodBase target = api.PatchTarget(point);
			Assert.NotNull(target);
			Assert.Equal(owner, target.DeclaringType.FullName);
		}
	}
}
