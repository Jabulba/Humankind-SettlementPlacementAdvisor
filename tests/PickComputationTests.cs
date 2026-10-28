using System;
using System.Linq;
using Amplitude.Mercury.Data.AI;
using Amplitude.Mercury.Data.Simulation;
using Amplitude.Mercury.Interop;
using SettlementPlacementAdvisor.gameapi;
using SettlementPlacementAdvisor.pipeline;
using Xunit;

namespace SettlementPlacementAdvisor.Tests
{
	/// <summary>
	///     T1: scan-pipeline rules on the scripted surface. Standard graph: territory 0 (hovered) {101,102,103},
	///     territory 1 {201,202,203}, edge 0-&gt;1; scores = tile index, so a territory's ranked order is its tiles
	///     descending. Quota 2/1, RangeLevels 2.
	/// </summary>
	/// <remarks>
	///     <para>
	///         Expected pick sets are literals derived from the pipeline contract, never read back from the code
	///         under test.
	///     </para>
	///     <para>
	///         The ScriptSurface counters make the shape of a pass observable, so the rows a scan cannot answer
	///         stay pinned: the army-level gate answers once per scan pass and never per tile; the pre-filter runs
	///         before the tile gate, so a dropped tile costs no gate call and a territory emptied by it costs the pass
	///         none; an unknown territory contributes no tile and asks no gate; a cache hit evaluates no tile, and a
	///         territory-frame bump alone evaluates none; an unanswered district definition, an unanswerable army-level
	///         gate and an unanswered scoring call each publish nothing.
	///     </para>
	///     <para>
	///         The counters bound the scan's reach, not its time: a rising counter is the drift signal for the
	///         5 ms per refresh bound stated on Advisor.
	///     </para>
	/// </remarks>
	public class PickComputationTests
	{
		private static CursorContext Context(ArmyActionType mode, int empireIndex = 0, int hovered = 0)
		{
			return CursorContext.For(hovered, new object(), empireIndex, 2, 1, 2, mode);
		}

		private static int[] Tiles(Pick[] picks)
		{
			return picks.PickTiles();
		}

		[Fact]
		public void NeighborhoodIsExactlyOneAdjacencyHop()
		{
			ScriptSurface surface = ScriptSurface.Standard().AddTerritory(2, 301, 302, 303).Link(1, 2);
			Pick[] picks = new PickComputation(surface).Compute(Context(ArmyActionType.CreateCamp));

			Assert.Equal(new[] { 102, 103, 203 }, Tiles(picks));
			// The frontier lands on territory 1; RangeLevels 2 never asks its adjacency.
			Assert.Equal(1, surface.AdjacencyCalls);
		}

		[Fact]
		public void EveryAdjacencyEdgeCountsNoTransitionFilter()
		{
			ScriptSurface surface = new ScriptSurface()
				.AddTerritory(0, 101, 102, 103)
				.AddTerritory(1, 201, 202, 203)
				.Link(0, 1, TerritoryTransition.Closed);
			Pick[] picks = new PickComputation(surface).Compute(Context(ArmyActionType.CreateCamp));

			Assert.Equal(new[] { 102, 103, 203 }, Tiles(picks));
		}

		[Fact]
		public void UnanswerableAdjacencyDegradesToHoveredOnlyAndLogs()
		{
			Probe.Start();
			ScriptSurface surface = ScriptSurface.Standard();
			surface.Territories[0].Edges = null;
			Pick[] picks = new PickComputation(surface).Compute(Context(ArmyActionType.CreateCamp));

			Assert.Equal(new[] { 102, 103 }, Tiles(picks));
			Assert.Contains(Probe.LogLines, line => line.Contains("placement-neighborhood adjacency: territory 0"));
		}

		[Fact]
		public void PrefilterDropDropsTheTileAndSavesTheGateCall()
		{
			Probe.Start();
			ScriptSurface surface = ScriptSurface.Standard();
			surface.Prefilter[103] = TileVerdict.Drop;
			Pick[] picks = new PickComputation(surface).Compute(Context(ArmyActionType.CreateCamp));

			// The pre-filter runs before the tile gate: 103 is gone, and its gate is never asked,
			// so the hovered quota drops to the next survivors.
			Assert.Equal(new[] { 101, 102, 203 }, Tiles(picks));
			Assert.Equal(5, surface.TileGateCalls);
			Assert.Equal(5, surface.EvaluateTileCalls);
		}

		[Fact]
		public void PrefilterEmptyingATerritoryCostsThePassNoGateCall()
		{
			Probe.Start();
			ScriptSurface surface = ScriptSurface.Standard();
			for (int tile = 201; tile <= 203; tile++)
			{
				surface.Prefilter[tile] = TileVerdict.Drop;
			}

			Pick[] picks = new PickComputation(surface).Compute(Context(ArmyActionType.CreateCamp));

			Assert.Equal(new[] { 102, 103 }, Tiles(picks));
			Assert.Equal(3, surface.TileGateCalls);
			Assert.Equal(3, surface.EvaluateTileCalls);
		}

		[Fact]
		public void ArmyLevelGateAnswersOncePerScanPassNeverPerTile()
		{
			Probe.Start();
			ScriptSurface surface = ScriptSurface.Standard();
			PickComputation computation = new PickComputation(surface);

			computation.Compute(Context(ArmyActionType.CreateCamp));
			Assert.Equal(new[] { 102, 103, 203 }, Tiles(computation.Compute(Context(ArmyActionType.CreateCamp))));

			// Two passes, two army-level gate answers, six tile-gate answers: the army-level gate is
			// not in the per-tile loop, and the second pass serves every tile from the cache.
			Assert.Equal(2, surface.ArmyGateCalls);
			Assert.Equal(6, surface.TileGateCalls);
			Assert.Equal(6, surface.EvaluateTileCalls);
		}

		[Fact]
		public void ClearCacheForcesAFullRescan()
		{
			Probe.Start();
			ScriptSurface surface = ScriptSurface.Standard();
			PickComputation computation = new PickComputation(surface);
			int[] first = Tiles(computation.Compute(Context(ArmyActionType.CreateCamp)));
			Assert.Equal(6, surface.EvaluateTileCalls);

			// Same stamps: the cache serves, and no tile is re-evaluated.
			Assert.Equal(first, Tiles(computation.Compute(Context(ArmyActionType.CreateCamp))));
			Assert.Equal(6, surface.EvaluateTileCalls);

			// The new game's reset: every territory of the neighborhood scores again.
			computation.ClearCache();
			Assert.Equal(first, Tiles(computation.Compute(Context(ArmyActionType.CreateCamp))));
			Assert.Equal(12, surface.EvaluateTileCalls);
		}

		[Fact]
		public void ClearCacheRescoresThePreviousGamesTerritory()
		{
			Probe.Start();
			ScriptSurface surface = ScriptSurface.Standard();
			PickComputation computation = new PickComputation(surface);
			Assert.Equal(new[] { 102, 103, 203 }, Tiles(computation.Compute(Context(ArmyActionType.CreateCamp))));

			// The new game's map with the stamps unchanged: territory 1 holds other tiles now. A
			// stored entry answers for territory 1 by index and stamp, so without the reset 203 comes
			// back as this game's recommendation.
			surface.Territories[1].Tiles = new[] { 501, 502, 503 };
			computation.ClearCache();
			Assert.Equal(new[] { 102, 103, 503 }, Tiles(computation.Compute(Context(ArmyActionType.CreateCamp))));
			Assert.Equal(12, surface.EvaluateTileCalls);
		}

		[Fact]
		public void CampDropsClaimedAndSettledTerritories()
		{
			ScriptSurface claimed = ScriptSurface.Standard();
			claimed.Territories[1].Claimed = true;
			Assert.Equal(new[] { 102, 103 },
				Tiles(new PickComputation(claimed).Compute(Context(ArmyActionType.CreateCamp))));

			ScriptSurface settled = ScriptSurface.Standard();
			settled.Territories[1].HasSettlement = true;
			Assert.Equal(new[] { 102, 103 },
				Tiles(new PickComputation(settled).Compute(Context(ArmyActionType.CreateCamp))));
		}

		[Fact]
		public void CityFoundingKeepsClaimedAndDropsSettled()
		{
			ScriptSurface claimed = ScriptSurface.Standard();
			claimed.Territories[1].Claimed = true;
			Assert.Equal(new[] { 102, 103, 203 },
				Tiles(new PickComputation(claimed).Compute(Context(ArmyActionType.FoundCity))));

			ScriptSurface settled = ScriptSurface.Standard();
			settled.Territories[1].HasSettlement = true;
			Assert.Equal(new[] { 102, 103 },
				Tiles(new PickComputation(settled).Compute(Context(ArmyActionType.FoundCity))));
		}

		[Fact]
		public void SettlerKeepsOwnCampSettlementOnly()
		{
			ScriptSurface ownCamp = ScriptSurface.Standard();
			ownCamp.Territories[1].HasSettlement = true;
			ownCamp.Territories[1].OwnerEmpireIndex = 0;
			Assert.Equal(new[] { 102, 103, 203 },
				Tiles(new PickComputation(ownCamp).Compute(Context(ArmyActionType.FoundCityWithSettler))));

			ScriptSurface ownCity = ScriptSurface.Standard();
			ownCity.Territories[1].HasSettlement = true;
			ownCity.Territories[1].OwnerEmpireIndex = 0;
			ownCity.Territories[1].IsOwnedByCity = true;
			Assert.Equal(new[] { 102, 103 },
				Tiles(new PickComputation(ownCity).Compute(Context(ArmyActionType.FoundCityWithSettler))));

			ScriptSurface otherEmpire = ScriptSurface.Standard();
			otherEmpire.Territories[1].HasSettlement = true;
			otherEmpire.Territories[1].OwnerEmpireIndex = 1;
			Assert.Equal(new[] { 102, 103 },
				Tiles(new PickComputation(otherEmpire).Compute(Context(ArmyActionType.FoundCityWithSettler))));
		}

		[Fact]
		public void WorldEdgeTerritoryIsAlwaysDropped()
		{
			ScriptSurface surface = ScriptSurface.Standard();
			surface.Territories[1].IsEdgeOfTheWorld = true;
			Assert.Equal(new[] { 102, 103 },
				Tiles(new PickComputation(surface).Compute(Context(ArmyActionType.CreateCamp))));

			ScriptSurface hoveredEdge = ScriptSurface.Standard();
			hoveredEdge.Territories[0].IsEdgeOfTheWorld = true;
			Assert.Equal(new[] { 203 },
				Tiles(new PickComputation(hoveredEdge).Compute(Context(ArmyActionType.CreateCamp))));
		}

		[Fact]
		public void GateMaskDropsEverythingExceptMoneyAndInfluence()
		{
			ScriptSurface money = ScriptSurface.Standard();
			money.TileGate[103] = ArmyActionFailureFlags_Part1.NotEnoughMoney;
			Assert.Equal(new[] { 102, 103, 203 },
				Tiles(new PickComputation(money).Compute(Context(ArmyActionType.CreateCamp))));

			ScriptSurface influence = ScriptSurface.Standard();
			influence.TileGate[103] = ArmyActionFailureFlags_Part1.NotEnoughInfluence;
			Assert.Equal(new[] { 102, 103, 203 },
				Tiles(new PickComputation(influence).Compute(Context(ArmyActionType.CreateCamp))));

			ScriptSurface units = ScriptSurface.Standard();
			units.TileGate[103] = ArmyActionFailureFlags_Part1.NotEnoughUnits;
			Assert.Equal(new[] { 101, 102, 203 },
				Tiles(new PickComputation(units).Compute(Context(ArmyActionType.CreateCamp))));
		}

		[Fact]
		public void UnaffordableTilesStayInTheAdmissionSet()
		{
			ScriptSurface surface = ScriptSurface.Standard();
			surface.TileGate[102] = ArmyActionFailureFlags_Part1.NotEnoughMoney;
			surface.TileGate[203] = ArmyActionFailureFlags_Part1.NotEnoughMoney;
			Pick[] picks = new PickComputation(surface).Compute(Context(ArmyActionType.CreateCamp));

			Assert.Equal(new[] { 102, 103, 203 }, Tiles(picks));
		}

		[Fact]
		public void ResourceTilesKeepTheirExclusion()
		{
			ScriptSurface surface = ScriptSurface.Standard();
			surface.ResourceTiles.Add(103);
			Assert.Equal(new[] { 101, 102, 203 },
				Tiles(new PickComputation(surface).Compute(Context(ArmyActionType.CreateCamp))));
		}

		[Fact]
		public void QuotaPlusFullTieSetNoCap()
		{
			ScriptSurface surface = ScriptSurface.Standard();
			for (int tile = 101; tile <= 103; tile++)
			{
				surface.ScoreOverride[tile] = 7;
			}

			for (int tile = 201; tile <= 203; tile++)
			{
				surface.ScoreOverride[tile] = 7;
			}

			Pick[] picks = new PickComputation(surface).Compute(Context(ArmyActionType.CreateCamp));

			// Quota 2 on a 3-tile tie admits all three; the neighbour quota 1 admits its top
			// ranked tile and the whole tie set behind it.
			Assert.Equal(new[] { 101, 102, 103, 201, 202, 203 }, Tiles(picks));
		}

		[Fact]
		public void MergedSetIsAscendingAndUniqueByTile()
		{
			// Territory 1 and territory 2 both list tile 203; each admits it as its top pick.
			ScriptSurface surface = new ScriptSurface()
				.AddTerritory(0, 101, 102, 103)
				.AddTerritory(1, 201, 202, 203)
				.AddTerritory(2, 90, 100, 203)
				.Link(0, 1)
				.Link(0, 2);
			Pick[] picks = new PickComputation(surface).Compute(Context(ArmyActionType.CreateCamp));

			Assert.Equal(new[] { 102, 103, 203 }, Tiles(picks));
		}

		[Fact]
		public void CacheServesUnchangedFramesAndClaimFacts()
		{
			Probe.Start();
			ScriptSurface surface = ScriptSurface.Standard();
			PickComputation computation = new PickComputation(surface);

			int[] first = Tiles(computation.Compute(Context(ArmyActionType.CreateCamp)));
			int callsAfterFirst = surface.EvaluateTileCalls;
			int[] second = Tiles(computation.Compute(Context(ArmyActionType.CreateCamp)));

			Assert.Equal(first, second);
			Assert.Equal(callsAfterFirst, surface.EvaluateTileCalls);

			// Territory-frame bump with unchanged claim stamps: served, no re-evaluation.
			surface.TerritoryFrame = 2;
			Assert.Equal(first, Tiles(computation.Compute(Context(ArmyActionType.CreateCamp))));
			Assert.Equal(callsAfterFirst, surface.EvaluateTileCalls);
		}

		[Fact]
		public void AdjacentCursorMoveIsServedFromTheCache()
		{
			Probe.Start();
			ScriptSurface surface = new ScriptSurface()
				.AddTerritory(0, 101, 102, 103)
				.AddTerritory(1, 201, 202, 203)
				.AddTerritory(2, 301, 302, 303)
				.LinkBoth(0, 1)
				.LinkBoth(1, 2);
			PickComputation computation = new PickComputation(surface);

			Assert.Equal(new[] { 102, 103, 203 }, Tiles(computation.Compute(Context(ArmyActionType.CreateCamp))));
			Assert.Equal(6, surface.EvaluateTileCalls);

			// Hovering the neighbour the previous pass already scanned: only the admission changes,
			// the newly adjacent territory is the one new scan.
			Assert.Equal(new[] { 103, 202, 203, 303 },
				Tiles(computation.Compute(Context(ArmyActionType.CreateCamp, hovered: 1))));
			Assert.Equal(9, surface.EvaluateTileCalls);
			Assert.Equal(2, surface.AdjacencyCalls);

			// Both remaining territories were scanned by the previous pass: pure cache hits.
			Assert.Equal(new[] { 203, 302, 303 },
				Tiles(computation.Compute(Context(ArmyActionType.CreateCamp, hovered: 2))));
			Assert.Equal(9, surface.EvaluateTileCalls);
			Assert.Equal(3, surface.AdjacencyCalls);

			// Crossing back over the border is a cache hit too: the quota role holds no key.
			Assert.Equal(new[] { 103, 202, 203, 303 },
				Tiles(computation.Compute(Context(ArmyActionType.CreateCamp, hovered: 1))));
			Assert.Equal(9, surface.EvaluateTileCalls);
			Assert.Equal(4, surface.AdjacencyCalls);
		}

		[Fact]
		public void TerritoryFrameBumpWithChangedClaimStampsRecomputes()
		{
			Probe.Start();
			ScriptSurface surface = ScriptSurface.Standard();
			PickComputation computation = new PickComputation(surface);
			computation.Compute(Context(ArmyActionType.CreateCamp));
			int factsAfterFirst = surface.FactsReads;

			surface.Territories[1].Claimed = true;
			surface.TerritoryFrame = 2;
			int[] picks = Tiles(computation.Compute(Context(ArmyActionType.CreateCamp)));

			// The stamp revalidation read the facts; the changed claim dropped the territory.
			Assert.Equal(new[] { 102, 103 }, picks);
			Assert.True(surface.FactsReads > factsAfterFirst);
		}

		[Fact]
		public void TerritoryFrameBumpWithUnchangedStampsReReadsFactsAndEvaluatesNoTile()
		{
			Probe.Start();
			ScriptSurface surface = ScriptSurface.Standard();
			PickComputation computation = new PickComputation(surface);
			computation.Compute(Context(ArmyActionType.CreateCamp));
			Assert.Equal(2, surface.FactsReads);

			surface.TerritoryFrame = 2;
			Assert.Equal(new[] { 102, 103, 203 }, Tiles(computation.Compute(Context(ArmyActionType.CreateCamp))));

			// Revalidation is a fact re-read, not a scan: one read per neighborhood territory,
			// and every tile served from the entry.
			Assert.Equal(4, surface.FactsReads);
			Assert.Equal(6, surface.EvaluateTileCalls);
		}

		[Fact]
		public void EveryStampedFactChangeRecomputesItsTerritory()
		{
			Probe.Start();
			// Each row moves one stamped fact under a territory-frame bump: the entry is read twice
			// for that territory - once to fail the stamp check, once to recompute - and the
			// admission set moves to the recomputed survivors.
			RecomputedOnStampChange(s => s.Territories[1].IsEdgeOfTheWorld = true, new[] { 102, 103 });
			RecomputedOnStampChange(s => s.Territories[1].Claimed = true, new[] { 102, 103 });
			RecomputedOnStampChange(s => s.Territories[1].HasSettlement = true, new[] { 102, 103 });
			RecomputedOnStampChange(s => s.Territories[1].IsOwnedByCity = true, new[] { 102, 103, 203 });
			RecomputedOnStampChange(s => s.Territories[1].OwnerEmpireIndex = 7, new[] { 102, 103, 203 });
			RecomputedOnStampChange(s => s.Territories[1].Tiles = new[] { 201, 202 }, new[] { 102, 103, 202 });
		}

		[Fact]
		public void EveryFrameBumpRecomputesTheRankedSurvivors()
		{
			Probe.Start();
			ScriptSurface surface = ScriptSurface.Standard();
			PickComputation computation = new PickComputation(surface);
			int[] first = Tiles(computation.Compute(Context(ArmyActionType.CreateCamp)));
			Assert.Equal(6, surface.EvaluateTileCalls);

			surface.DistrictFrame = 2;
			Assert.Equal(first, Tiles(computation.Compute(Context(ArmyActionType.CreateCamp))));
			Assert.Equal(12, surface.EvaluateTileCalls);

			surface.ConstructionFrame = 2;
			Assert.Equal(first, Tiles(computation.Compute(Context(ArmyActionType.CreateCamp))));
			Assert.Equal(18, surface.EvaluateTileCalls);

			surface.VisibilityFrame = 2;
			Assert.Equal(first, Tiles(computation.Compute(Context(ArmyActionType.CreateCamp))));
			Assert.Equal(24, surface.EvaluateTileCalls);
		}

		[Fact]
		public void EmpireChangeRecomputesTheRankedSurvivors()
		{
			Probe.Start();
			ScriptSurface surface = ScriptSurface.Standard();
			PickComputation computation = new PickComputation(surface);
			computation.Compute(Context(ArmyActionType.CreateCamp));
			Assert.Equal(6, surface.EvaluateTileCalls);

			// Same frames, same territory facts, another empire: the empire index is part of the
			// entry's stamps, so a new empire never serves the entry scored for the old one.
			Tiles(computation.Compute(Context(ArmyActionType.CreateCamp, 1)));
			Assert.Equal(12, surface.EvaluateTileCalls);
		}

		private static void RecomputedOnStampChange(Action<ScriptSurface> change, int[] expectedPicks)
		{
			ScriptSurface surface = ScriptSurface.Standard();
			PickComputation computation = new PickComputation(surface);
			computation.Compute(Context(ArmyActionType.CreateCamp));
			surface.TerritoryFrame = 2;
			computation.Compute(Context(ArmyActionType.CreateCamp));
			Assert.Equal(4, surface.FactsReads);
			Assert.Equal(6, surface.EvaluateTileCalls);

			change(surface);
			surface.TerritoryFrame = 3;
			int[] picks = Tiles(computation.Compute(Context(ArmyActionType.CreateCamp)));

			// The stamp check reads the facts once; the recompute reads them again for the territory
			// whose stamp moved: 4 + 1 (hovered revalidation) + 2 (recomputed territory).
			Assert.Equal(7, surface.FactsReads);
			Assert.Equal(expectedPicks, picks);
		}

		[Fact]
		public void ModeIsPartOfTheCacheKey()
		{
			Probe.Start();
			ScriptSurface surface = ScriptSurface.Standard();
			surface.Territories[1].Claimed = true;
			PickComputation computation = new PickComputation(surface);
			computation.Compute(Context(ArmyActionType.CreateCamp));
			int callsAfterFirst = surface.EvaluateTileCalls;

			// Same frames, same territory facts, different mode: the camp entry must not serve
			// the city row's claim rules.
			int[] city = Tiles(computation.Compute(Context(ArmyActionType.FoundCity)));

			Assert.Equal(new[] { 102, 103, 203 }, city);
			Assert.True(surface.EvaluateTileCalls > callsAfterFirst);
		}

		[Fact]
		public void UnansweredFramesAndUnwiredModePublishNothing()
		{
			Probe.Start();
			ScriptSurface noFrames = ScriptSurface.Standard();
			noFrames.FramesAnswer = false;
			Assert.Null(new PickComputation(noFrames).Compute(Context(ArmyActionType.CreateCamp)));

			ScriptSurface unwired = ScriptSurface.Standard();
			Assert.Null(new PickComputation(unwired).Compute(Context(ArmyActionType.Ransack)));
		}

		[Fact]
		public void UnknownTerritoryFactsContributeNoTileAndAskNoGate()
		{
			Probe.Start();
			// Territory 7 is on the hovered territory's adjacency list and the surface has no facts
			// for it: fail closed, and spend no gate call on it.
			ScriptSurface surface = new ScriptSurface()
				.AddTerritory(0, 101, 102, 103)
				.Link(0, 7);
			Pick[] picks = new PickComputation(surface).Compute(Context(ArmyActionType.CreateCamp));

			Assert.Equal(new[] { 102, 103 }, Tiles(picks));
			Assert.Equal(3, surface.TileGateCalls);
			Assert.Equal(3, surface.EvaluateTileCalls);
		}

		[Fact]
		public void UnansweredDistrictDefinitionPublishesNothing()
		{
			Probe.Start();
			ScriptSurface surface = ScriptSurface.Standard();
			surface.DistrictDefinitionAnswer = false;
			Pick[] picks = new PickComputation(surface).Compute(Context(ArmyActionType.CreateCamp));

			Assert.Null(picks);
			Assert.Equal(0, surface.TileGateCalls);
			Assert.Equal(0, surface.EvaluateTileCalls);
		}

		[Fact]
		public void UnanswerableArmyGatePublishesNothing()
		{
			Probe.Start();
			ScriptSurface surface = ScriptSurface.Standard();
			surface.ArmyGateAnswered = false;
			Pick[] picks = new PickComputation(surface).Compute(Context(ArmyActionType.CreateCamp));

			Assert.Null(picks);
			Assert.Equal(1, surface.ArmyGateCalls);
			Assert.Equal(0, surface.TileGateCalls);
		}

		[Fact]
		public void UnanswerableScoringDropsEveryTileItWasAskedFor()
		{
			Probe.Start();
			ScriptSurface surface = ScriptSurface.Standard();
			surface.EvaluateTileAnswered = false;
			Pick[] picks = new PickComputation(surface).Compute(Context(ArmyActionType.CreateCamp));

			Assert.Empty(picks);
			Assert.Equal(6, surface.TileGateCalls);
			Assert.Equal(6, surface.EvaluateTileCalls);
		}

		[Fact]
		public void ArmyGateHitSkipsTheWholeScan()
		{
			Probe.Start();
			ScriptSurface surface = ScriptSurface.Standard();
			surface.ArmyGateFailures = ArmyActionFailureFlags_Part1.NotEnoughMovement;
			Pick[] picks = new PickComputation(surface).Compute(Context(ArmyActionType.CreateCamp));

			Assert.Empty(picks);
			Assert.Equal(0, surface.TileGateCalls);
		}

		[Fact]
		public void UnanswerableTileGateDropsTheTile()
		{
			Probe.Start();
			ScriptSurface surface = ScriptSurface.Standard();
			surface.TileGateAnswered = false;
			Pick[] picks = new PickComputation(surface).Compute(Context(ArmyActionType.CreateCamp));

			Assert.Empty(picks);
		}
	}

	internal static class TestExtensions
	{
		public static int[] PickTiles(this Pick[] picks)
		{
			return picks.Select(pick => pick.Tile).ToArray();
		}
	}
}
