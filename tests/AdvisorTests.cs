using System.Collections.Generic;
using Amplitude;
using Amplitude.Mercury.Data.Simulation;
using Amplitude.Mercury.Interop;
using Amplitude.Mercury.Simulation;
using SettlementPlacementAdvisor.pipeline;
using SettlementPlacementAdvisor.presentation;
using Xunit;

namespace SettlementPlacementAdvisor.Tests
{
	/// <summary>
	///     T1: the Advisor chain (dispatch, army gate, publish outcomes) on the scripted surface, writing into a
	///     real CampPlacementEvaluation.
	/// </summary>
	public class AdvisorTests
	{
		private static readonly SimulationEntityGUID Army = new SimulationEntityGUID(1UL);

		private static readonly List<int> StandardPicks = new List<int> { 102, 103, 203 };

		private static Advisor Graph(ScriptSurface surface, out CampPlacementEvaluation evaluation)
		{
			Probe.Start();
			surface.CursorTile = 101;
			evaluation = Probe.NewEvaluation(4, ArmyActionType.CreateCamp);
			EvaluationWriter writer = new EvaluationWriter(surface);
			return new Advisor(surface, new PickComputation(surface), writer);
		}

		[Fact]
		public void ScanPassPublishesWithoutWriting()
		{
			Advisor advisor = Graph(ScriptSurface.Standard(), out CampPlacementEvaluation evaluation);

			advisor.RunScanPass(evaluation, 101, Army);

			Assert.True(PresentationView.Current.IsActive);
			foreach (int tile in StandardPicks)
			{
				Assert.True(PresentationView.Current.Contains(tile));
			}

			Assert.Equal(PresentationView.NoCursorTile, PresentationView.Current.CursorTile);
			Assert.Equal(4, evaluation.ValidTileCount);
			Assert.Equal(-1, evaluation.CurrentPositionIndex);
		}

		[Fact]
		public void ArmyGateHitSkipsScanAndPublishesInactive()
		{
			ScriptSurface surface = ScriptSurface.Standard();
			surface.ArmyGateFailures = ArmyActionFailureFlags_Part1.NotEnoughMovement;
			Advisor advisor = Graph(surface, out CampPlacementEvaluation evaluation);

			advisor.RunScanPass(evaluation, 101, Army);

			Assert.False(PresentationView.Current.IsActive);
			Assert.Equal(4, evaluation.ValidTileCount);
			Assert.Equal(0, surface.TileGateCalls);
		}

		[Fact]
		public void RefreshWritesPicksCursorAndPublishes()
		{
			Advisor advisor = Graph(ScriptSurface.Standard(), out CampPlacementEvaluation evaluation);

			advisor.Run(evaluation, 101, Army);

			// Tail: cursor slot at the window start, then the picks; vanilla rows untouched.
			Assert.Equal(8, evaluation.ValidTileCount);
			Assert.Equal(4, evaluation.CurrentPositionIndex);
			Assert.Equal(101, evaluation.ValidTiles[4].TileIndex);
			for (int i = 0; i < 4; i++)
			{
				Assert.Equal(5000 + i, evaluation.ValidTiles[i].TileIndex);
				Assert.False(evaluation.ValidTiles[i].Showable);
			}

			Assert.True(evaluation.ValidTiles[4].Showable);
			for (int i = 0; i < StandardPicks.Count; i++)
			{
				Assert.Equal(StandardPicks[i], evaluation.ValidTiles[5 + i].TileIndex);
				Assert.True(evaluation.ValidTiles[5 + i].Showable);
			}

			Assert.True(PresentationView.Current.IsActive);
			Assert.Equal(101, PresentationView.Current.CursorTile);
			Assert.Equal(ArmyActionFailureFlags.None, PresentationView.Current.SettleFailure);
		}

		[Fact]
		public void CostFailureSurfacesOnTheCursorSlotOnly()
		{
			ScriptSurface surface = ScriptSurface.Standard();
			surface.TileGate[101] = ArmyActionFailureFlags_Part1.NotEnoughInfluence;
			Advisor advisor = Graph(surface, out CampPlacementEvaluation evaluation);

			advisor.Run(evaluation, 101, Army);

			// The hovered rule hides the cursor marker only for failures beyond influence.
			Assert.True(evaluation.ValidTiles[4].Showable);
			Assert.Equal(ArmyActionFailureFlags_Part1.NotEnoughInfluence,
				PresentationView.Current.SettleFailure);
		}

		[Fact]
		public void UnaffordableHoveredTileLosesItsGlyphAndTheShownSetKeepsIts()
		{
			ScriptSurface surface = ScriptSurface.Standard();
			surface.TileGate[101] = ArmyActionFailureFlags_Part1.NotEnoughMoney;
			surface.TileGate[102] = ArmyActionFailureFlags_Part1.NotEnoughMoney;
			Advisor advisor = Graph(surface, out CampPlacementEvaluation evaluation);

			advisor.Run(evaluation, 101, Army);

			// The hovered rule hides the cursor glyph on a money failure; 102 is both a shown tile
			// and unaffordable, and keeps its glyph: affordability never filters a pick.
			Assert.False(evaluation.ValidTiles[4].Showable);
			for (int i = 0; i < StandardPicks.Count; i++)
			{
				Assert.Equal(StandardPicks[i], evaluation.ValidTiles[5 + i].TileIndex);
				Assert.True(evaluation.ValidTiles[5 + i].Showable);
			}

			Assert.True(PresentationView.Current.IsActive);
			Assert.Equal(ArmyActionFailureFlags_Part1.NotEnoughMoney,
				PresentationView.Current.SettleFailure);
		}

		[Fact]
		public void VanillaBestCampPlacementArraysStayStumpedThroughARefresh()
		{
			Advisor advisor = Graph(ScriptSurface.Standard(), out CampPlacementEvaluation evaluation);

			advisor.Run(evaluation, 101, Army);

			// Vanilla stamps these at -1 / MinValue at activation; the advisor writes the tail and
			// leaves them alone - no suppression loop, no best-camp bookkeeping.
			foreach (int tile in evaluation.BestCampPlacementTileIndexes)
			{
				Assert.Equal(-1, tile);
			}

			foreach (int index in evaluation.BestCampPlacementIndexes)
			{
				Assert.Equal(-1, index);
			}

			foreach (FixedPoint score in evaluation.BestCampPlacementScores)
			{
				Assert.Equal(FixedPoint.MinValue, score);
			}
		}

		[Fact]
		public void NoPicksPublishesEmptyAndScrubsTheTail()
		{
			ScriptSurface surface = ScriptSurface.Standard();
			surface.Territories[0].Claimed = true;
			surface.Territories[1].Claimed = true;
			Advisor advisor = Graph(surface, out CampPlacementEvaluation evaluation);

			advisor.Run(evaluation, 101, Army);

			Assert.False(PresentationView.Current.IsActive);
			Assert.Equal(-1, evaluation.CurrentPositionIndex);
			Assert.Equal(-1, evaluation.ValidTiles[4].TileIndex);
			Assert.False(evaluation.ValidTiles[4].Showable);
		}

		[Fact]
		public void MissingArmyPublishesInactiveEmpty()
		{
			ScriptSurface surface = ScriptSurface.Standard();
			surface.Army = null;
			Advisor advisor = Graph(surface, out CampPlacementEvaluation evaluation);

			advisor.Run(evaluation, 101, Army);

			Assert.False(PresentationView.Current.IsActive);
			Assert.Equal(-1, evaluation.CurrentPositionIndex);
		}

		[Fact]
		public void UnmappedModeWritesNothingAndPublishesInactive()
		{
			Advisor advisor = Graph(ScriptSurface.Standard(), out CampPlacementEvaluation evaluation);
			evaluation.SettleType = ArmyActionType.Ransack;

			advisor.Run(evaluation, 101, Army);

			Assert.False(PresentationView.Current.IsActive);
			Assert.Equal(4, evaluation.ValidTileCount);
			Assert.Equal(-1, evaluation.CurrentPositionIndex);
			foreach (DistrictPlacementEvaluation.ValidTile entry in evaluation.ValidTiles)
			{
				Assert.True(entry.TileIndex >= 5000);
			}
		}

		[Fact]
		public void PublishEmptyScrubsTheWholeWrittenTail()
		{
			Advisor advisor = Graph(ScriptSurface.Standard(), out CampPlacementEvaluation evaluation);
			advisor.Run(evaluation, 101, Army);
			Assert.Equal(8, evaluation.ValidTileCount);

			advisor.PublishEmpty(evaluation);

			Assert.False(PresentationView.Current.IsActive);
			Assert.Equal(-1, evaluation.CurrentPositionIndex);
			for (int i = 4; i < 8; i++)
			{
				Assert.Equal(-1, evaluation.ValidTiles[i].TileIndex);
				Assert.False(evaluation.ValidTiles[i].Showable);
			}

			Assert.Equal(8, evaluation.ValidTileCount);
		}

		[Fact]
		public void CursorOverNoTerritoryPublishesInactiveAndScrubsTheTail()
		{
			Advisor advisor = Graph(ScriptSurface.Standard(), out CampPlacementEvaluation evaluation);
			advisor.Run(evaluation, 101, Army);
			Assert.Equal(8, evaluation.ValidTileCount);

			// No territory owns 4004: the pass has no neighborhood, so it publishes empty and the
			// tail the previous refresh wrote is scrubbed - no white hex survives an empty pass.
			advisor.Run(evaluation, 4004, Army);

			Assert.False(PresentationView.Current.IsActive);
			Assert.Equal(-1, evaluation.CurrentPositionIndex);
			for (int i = 4; i < 8; i++)
			{
				Assert.Equal(-1, evaluation.ValidTiles[i].TileIndex);
				Assert.False(evaluation.ValidTiles[i].Showable);
			}
		}

		[Fact]
		public void UnanswerableScoringPublishesInactiveAndScrubsTheTail()
		{
			ScriptSurface surface = ScriptSurface.Standard();
			Advisor advisor = Graph(surface, out CampPlacementEvaluation evaluation);
			advisor.Run(evaluation, 101, Army);
			Assert.Equal(8, evaluation.ValidTileCount);

			// A frame bump takes the entry down, so this pass really asks the game to score the
			// tiles again - and gets no answer for any of them.
			surface.DistrictFrame = 2;
			surface.EvaluateTileAnswered = false;
			advisor.Run(evaluation, 101, Army);

			Assert.False(PresentationView.Current.IsActive);
			Assert.Equal(-1, evaluation.CurrentPositionIndex);
			for (int i = 4; i < 8; i++)
			{
				Assert.Equal(-1, evaluation.ValidTiles[i].TileIndex);
				Assert.False(evaluation.ValidTiles[i].Showable);
			}
		}

		[Fact]
		public void EachWiredModeRefreshesThroughItsOwnModeRow()
		{
			ScriptSurface surface = ScriptSurface.Standard();
			surface.Territories[1].Claimed = true;

			// Claimed is the camp row's filter alone: the city rows keep the claimed neighbour, and
			// the refresh publishes each mode's own admission set.
			Assert.Equal(new[] { 102, 103 }, Refresh(surface, ArmyActionType.CreateCamp));
			Assert.Equal(new[] { 102, 103, 203 }, Refresh(surface, ArmyActionType.FoundCity));
			Assert.Equal(new[] { 102, 103, 203 }, Refresh(surface, ArmyActionType.FoundCityWithSettler));
		}

		[Fact]
		public void SettlerModeKeepsItsOwnSettlementTerritoryOnTheRefresh()
		{
			ScriptSurface surface = ScriptSurface.Standard();
			surface.Territories[1].HasSettlement = true;
			surface.Territories[1].OwnerEmpireIndex = 1;

			// Empire 1 is the placing army's own empire, read off the army through the game surface.
			Assert.Equal(new[] { 102, 103 }, Refresh(surface, ArmyActionType.CreateCamp));
			Assert.Equal(new[] { 102, 103 }, Refresh(surface, ArmyActionType.FoundCity));
			Assert.Equal(new[] { 102, 103, 203 }, Refresh(surface, ArmyActionType.FoundCityWithSettler));
		}

		[Fact]
		public void OwnsRefreshFollowsTheModeTable()
		{
			Probe.Start();
			Assert.True(Advisor.OwnsRefresh(Probe.NewEvaluation(4, ArmyActionType.CreateCamp)));
			Assert.True(Advisor.OwnsRefresh(Probe.NewEvaluation(4, ArmyActionType.FoundCity)));
			Assert.True(Advisor.OwnsRefresh(Probe.NewEvaluation(4, ArmyActionType.FoundCityWithSettler)));
			Assert.False(Advisor.OwnsRefresh(Probe.NewEvaluation(4, ArmyActionType.Ransack)));
			Assert.False(Advisor.OwnsRefresh(Probe.NewEvaluation(4, ArmyActionType.AttackArmy)));
		}

		// One refresh on a fresh evaluation, read back off the tail the writer owns: the rows after
		// the cursor slot are the published pick set.
		private static int[] Refresh(ScriptSurface surface, ArmyActionType mode)
		{
			Probe.Start();
			surface.CursorTile = 101;
			CampPlacementEvaluation evaluation = Probe.NewEvaluation(4, mode);
			EvaluationWriter writer = new EvaluationWriter(surface);
			new Advisor(surface, new PickComputation(surface), writer).Run(evaluation, 101, Army);

			Assert.Equal(mode, PresentationView.Current.Mode);
			Assert.True(PresentationView.Current.IsActive);

			int[] picks = new int[evaluation.ValidTileCount - 5];
			for (int i = 5; i < evaluation.ValidTileCount; i++)
			{
				picks[i - 5] = evaluation.ValidTiles[i].TileIndex;
			}

			return picks;
		}
	}
}
