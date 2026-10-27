using System.Collections.Generic;
using Amplitude;
using Amplitude.Mercury.Data.Simulation;
using Amplitude.Mercury.Interop;
using SettlementPlacementAdvisor.composition;
using SettlementPlacementAdvisor.pipeline;
using Xunit;

namespace SettlementPlacementAdvisor.Tests
{
	/// <summary>
	///     T1: the write-side contract on a real CampPlacementEvaluation.
	/// </summary>
	/// <remarks>
	///     <para>Tail capacity at the bind defaults (Probe.ResetConfig): (1 + 8*(2-1)) * (2 + 8) = 90.</para>
	/// </remarks>
	public class EvaluationWriterTests
	{
		[Fact]
		public void ActivationReservesTheTailWindowAndScrubsIt()
		{
			Probe.Start();
			EvaluationWriter writer = new EvaluationWriter(ScriptSurface.Standard());
			CampPlacementEvaluation evaluation = Probe.NewEvaluation(4, ArmyActionType.CreateCamp);

			writer.NoteActivation(evaluation);

			Assert.Equal(4, evaluation.ValidTileCount);
			Assert.Equal(4 + 1 + 90, evaluation.ValidTiles.Length);
			for (int i = 4; i < evaluation.ValidTiles.Length; i++)
			{
				Assert.Equal(-1, evaluation.ValidTiles[i].TileIndex);
				Assert.False(evaluation.ValidTiles[i].Showable);
			}
		}

		[Fact]
		public void ActivationIgnoresUnwiredModes()
		{
			Probe.Start();
			EvaluationWriter writer = new EvaluationWriter(ScriptSurface.Standard());
			CampPlacementEvaluation evaluation = Probe.NewEvaluation(4, ArmyActionType.Ransack);

			writer.NoteActivation(evaluation);

			Assert.Equal(4, evaluation.ValidTiles.Length);
			Assert.Equal(4, evaluation.ValidTileCount);
		}

		[Fact]
		public void ReActivationReleasesThePreviousWindowWhole()
		{
			Probe.Start();
			ScriptSurface surface = ScriptSurface.Standard();
			EvaluationWriter writer = new EvaluationWriter(surface);
			CampPlacementEvaluation evaluation = Probe.NewEvaluation(4, ArmyActionType.CreateCamp);
			writer.NoteActivation(evaluation);
			writer.Write(evaluation, Picks(102, 103), AnsweredCursor(101));
			Assert.Equal(7, evaluation.ValidTileCount);

			writer.NoteActivation(evaluation);

			// The old region [4, reservedEnd) is scrubbed over the whole region, the new window
			// starts at the current count.
			// Releasing scrubs [4, 95); the new baseline (count 7) extends the region to 98.
			Assert.Equal(7 + 1 + 90, evaluation.ValidTiles.Length);
			for (int i = 4; i < 7; i++)
			{
				Assert.Equal(-1, evaluation.ValidTiles[i].TileIndex);
				Assert.False(evaluation.ValidTiles[i].Showable);
			}

			Assert.Equal(7, evaluation.ValidTileCount);
		}

		[Fact]
		public void EndedSessionsLeaveNoPinnedEvaluation()
		{
			Probe.Start();
			EvaluationWriter writer = new EvaluationWriter(ScriptSurface.Standard());

			// Ten placement sessions, each activated, written and cleared. The game drops an evaluation
			// when its session ends, so the writer's record for it has to die with it: a window table
			// holding the evaluation strongly would pin every evaluation the advisor ever wrote, the
			// tile arrays included.
			System.WeakReference<CampPlacementEvaluation>[] ended = WriteSessions(writer, 10);
			System.GC.Collect();
			System.GC.WaitForPendingFinalizers();
			System.GC.Collect();

			for (int i = 0; i < ended.Length; i++)
			{
				CampPlacementEvaluation survivor;
				Assert.False(ended[i].TryGetTarget(out survivor));
			}
		}

		[Fact]
		public void WritePlacesCursorThenPicksAndNeverMovesVanillaRows()
		{
			Probe.Start();
			EvaluationWriter writer = new EvaluationWriter(ScriptSurface.Standard());
			CampPlacementEvaluation evaluation = Probe.NewEvaluation(4, ArmyActionType.CreateCamp);
			writer.NoteActivation(evaluation);

			writer.Write(evaluation, Picks(102, 103, 203), AnsweredCursor(101));

			for (int i = 0; i < 4; i++)
			{
				Assert.Equal(5000 + i, evaluation.ValidTiles[i].TileIndex);
			}

			Assert.Equal(101, evaluation.ValidTiles[4].TileIndex);
			Assert.Equal(102, evaluation.ValidTiles[5].TileIndex);
			Assert.Equal(103, evaluation.ValidTiles[6].TileIndex);
			Assert.Equal(203, evaluation.ValidTiles[7].TileIndex);
			for (int i = 4; i <= 7; i++)
			{
				Assert.True(evaluation.ValidTiles[i].Showable);
			}

			Assert.Equal(8, evaluation.ValidTileCount);
			Assert.Equal(4, evaluation.CurrentPositionIndex);
			Assert.True(evaluation.ValidTiles[4].Showable);
		}

		[Fact]
		public void ShrinkingRefreshZeroesOnlyTheSlotsItReleases()
		{
			Probe.Start();
			EvaluationWriter writer = new EvaluationWriter(ScriptSurface.Standard());
			CampPlacementEvaluation evaluation = Probe.NewEvaluation(4, ArmyActionType.CreateCamp);
			writer.NoteActivation(evaluation);
			writer.Write(evaluation, Picks(102, 103, 203), AnsweredCursor(101));

			writer.Write(evaluation, Picks(102), AnsweredCursor(101));

			// Only the slots this refresh released are zeroed; the remaining reserved region stays
			// scrubbed from activation, and the new pick keeps its slot.
			Assert.Equal(102, evaluation.ValidTiles[5].TileIndex);
			Assert.True(evaluation.ValidTiles[5].Showable);
			for (int i = 6; i < 8; i++)
			{
				Assert.Equal(-1, evaluation.ValidTiles[i].TileIndex);
				Assert.False(evaluation.ValidTiles[i].Showable);
			}

			Assert.Equal(8, evaluation.ValidTileCount);
		}

		[Fact]
		public void WritesBeyondTheReservedRegionGrowIt()
		{
			Probe.Start();
			EvaluationWriter writer = new EvaluationWriter(ScriptSurface.Standard());
			CampPlacementEvaluation evaluation = Probe.NewEvaluation(4, ArmyActionType.CreateCamp);
			writer.NoteActivation(evaluation);
			int before = evaluation.ValidTiles.Length;

			List<Pick> picks = new List<Pick>();
			for (int tile = 600; tile < 600 + 100; tile++)
			{
				picks.Add(MakePick(tile));
			}

			writer.Write(evaluation, picks.ToArray(), AnsweredCursor(101));

			Assert.True(evaluation.ValidTiles.Length > before);
			Assert.Equal(4 + 1 + picks.Count, evaluation.ValidTileCount);
		}

		[Fact]
		public void ModeSwitchScrubsThePreviousModesTail()
		{
			Probe.Start();
			EvaluationWriter writer = new EvaluationWriter(ScriptSurface.Standard());
			CampPlacementEvaluation evaluation = Probe.NewEvaluation(4, ArmyActionType.CreateCamp);
			writer.NoteActivation(evaluation);
			writer.Write(evaluation, Picks(102, 103, 203), AnsweredCursor(101));

			evaluation.SettleType = ArmyActionType.Ransack;
			writer.NoteActivation(evaluation);

			for (int i = 4; i < evaluation.ValidTiles.Length; i++)
			{
				Assert.Equal(-1, evaluation.ValidTiles[i].TileIndex);
				Assert.False(evaluation.ValidTiles[i].Showable);
			}
		}

		[Fact]
		public void ReserveBaselineRespectsTheActivationAnswer()
		{
			Probe.Start();
			ScriptSurface surface = ScriptSurface.Standard();
			surface.ActivateAnswer = false;
			EvaluationWriter writer = new EvaluationWriter(surface);
			CampPlacementEvaluation evaluation = Probe.NewEvaluation(4, ArmyActionType.CreateCamp);

			writer.ReserveBaseline(evaluation);

			Assert.Equal(4, evaluation.ValidTiles.Length);
			Assert.Equal(4, evaluation.ValidTileCount);
		}

		[Fact]
		public void WriteCarriesTheCursorCostFactsOntoTheEvaluation()
		{
			Probe.Start();
			EvaluationWriter writer = new EvaluationWriter(ScriptSurface.Standard());
			CampPlacementEvaluation evaluation = Probe.NewEvaluation(4, ArmyActionType.CreateCamp);
			writer.NoteActivation(evaluation);

			CursorSlot cursor = AnsweredCursor(101);
			cursor.Failure = ArmyActionFailureFlags_Part1.NotEnoughInfluence;
			cursor.MoneyCost = 7 * FixedPoint.One;
			cursor.InfluenceCost = FixedPoint.One;
			cursor.UnitCost = 3 * FixedPoint.One;
			writer.Write(evaluation, Picks(102), cursor);

			Assert.Equal(cursor.Failure, evaluation.SettleFailure);
			Assert.Equal(cursor.MoneyCost, evaluation.InstantMoneyCost);
			Assert.Equal(cursor.InfluenceCost, evaluation.InstantInfluenceCost);
			Assert.Equal(cursor.UnitCost, evaluation.InstantUnitCost);
		}

		[Fact]
		public void ConditionFlagsGrowWithTheTailAndLeaveVanillasRowsAlone()
		{
			Probe.Start();
			EvaluationWriter writer = new EvaluationWriter(ScriptSurface.Standard());
			CampPlacementEvaluation evaluation = Probe.NewEvaluation(4, ArmyActionType.CreateCamp);

			// Vanilla's own rows carry real district conditions; the advisor owns the tail only.
			DistrictPlacementConditionFlags[] vanilla =
			{
				DistrictPlacementConditionFlags.DistrictAlreadyOnTile, DistrictPlacementConditionFlags.RazeCamp,
				DistrictPlacementConditionFlags.RazeExtension, DistrictPlacementConditionFlags.None
			};
			for (int i = 0; i < vanilla.Length; i++)
			{
				evaluation.ConditionalFlagsTileIndexes[i] = vanilla[i];
			}

			writer.NoteActivation(evaluation);
			writer.Write(evaluation, Picks(102, 103, 203), AnsweredCursor(101));

			Assert.Equal(evaluation.ValidTiles.Length, evaluation.ConditionalFlagsTileIndexes.Length);
			for (int i = 0; i < vanilla.Length; i++)
			{
				Assert.Equal(vanilla[i], evaluation.ConditionalFlagsTileIndexes[i]);
			}

			for (int i = 4; i <= 7; i++)
			{
				Assert.Equal(DistrictPlacementConditionFlags.None, evaluation.ConditionalFlagsTileIndexes[i]);
			}
		}

		[Fact]
		public void PickRowsAreAnsweredNoDistrictConditionWhereTheGameStampedOne()
		{
			Probe.Start();
			EvaluationWriter writer = new EvaluationWriter(ScriptSurface.Standard());
			CampPlacementEvaluation evaluation = Probe.NewEvaluation(4, ArmyActionType.CreateCamp);
			writer.NoteActivation(evaluation);

			// A district condition stamped on a tail slot the next refresh turns into a pick slot:
			// the writer answers it as no district condition, it does not inherit the old slot's flags.
			evaluation.ConditionalFlagsTileIndexes[6] = DistrictPlacementConditionFlags.DistrictAlreadyOnTile;
			writer.Write(evaluation, Picks(102, 103, 203), AnsweredCursor(101));

			Assert.Equal(DistrictPlacementConditionFlags.None, evaluation.ConditionalFlagsTileIndexes[6]);
			Assert.Equal(103, evaluation.ValidTiles[6].TileIndex);
		}

		[Fact]
		public void TheCursorSlotAnswersNoDistrictConditionForVanillasHoveredCursor()
		{
			Probe.Start();
			EvaluationWriter writer = new EvaluationWriter(ScriptSurface.Standard());
			CampPlacementEvaluation evaluation = Probe.NewEvaluation(4, ArmyActionType.CreateCamp);
			writer.NoteActivation(evaluation);

			writer.Write(evaluation, Picks(102), AnsweredCursor(101));

			// Vanilla's hovered cursor reads the condition flags through CurrentPositionIndex: the
			// advisor answers the cursor slot as no district condition.
			Assert.Equal(4, evaluation.CurrentPositionIndex);
			Assert.Equal(DistrictPlacementConditionFlags.None, evaluation.GetCurrentPositionConditionFlags());
		}

		[Fact]
		public void ShrinkingRefreshZeroesTheReleasedConditionFlagsRow()
		{
			Probe.Start();
			EvaluationWriter writer = new EvaluationWriter(ScriptSurface.Standard());
			CampPlacementEvaluation evaluation = Probe.NewEvaluation(4, ArmyActionType.CreateCamp);
			writer.NoteActivation(evaluation);
			writer.Write(evaluation, Picks(102, 103, 203), AnsweredCursor(101));

			evaluation.ConditionalFlagsTileIndexes[7] = DistrictPlacementConditionFlags.RazeCamp;
			writer.Write(evaluation, Picks(102), AnsweredCursor(101));

			// Zero-fill covers the condition-flags array on the same indices it zeroes in ValidTiles.
			Assert.Equal(DistrictPlacementConditionFlags.None, evaluation.ConditionalFlagsTileIndexes[7]);
			Assert.Equal(102, evaluation.ValidTiles[5].TileIndex);
		}

		// NoInlining so the evaluations die with the frame: the row's collect has to see them
		// unreachable, with no live local keeping one alive.
		[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
		private static System.WeakReference<CampPlacementEvaluation>[] WriteSessions(EvaluationWriter writer, int sessions)
		{
			System.WeakReference<CampPlacementEvaluation>[] references = new System.WeakReference<CampPlacementEvaluation>[sessions];
			for (int i = 0; i < sessions; i++)
			{
				CampPlacementEvaluation evaluation = Probe.NewEvaluation(4, ArmyActionType.CreateCamp);
				references[i] = new System.WeakReference<CampPlacementEvaluation>(evaluation);
				writer.NoteActivation(evaluation);
				writer.Write(evaluation, Picks(102, 103), AnsweredCursor(101));
				writer.ClearTail(evaluation);
			}

			return references;
		}

		internal static Pick MakePick(int tile)
		{
			DistrictPlacementEvaluation.ValidTile entry = default;
			entry.Initialize(tile);
			return new Pick { Tile = tile, Score = tile * FixedPoint.One, Data = entry };
		}

		private static Pick[] Picks(params int[] tiles)
		{
			Pick[] picks = new Pick[tiles.Length];
			for (int i = 0; i < tiles.Length; i++)
			{
				picks[i] = MakePick(tiles[i]);
			}

			return picks;
		}

		private static CursorSlot AnsweredCursor(int tile)
		{
			DistrictPlacementEvaluation.ValidTile entry = default;
			entry.Initialize(tile);
			entry.Showable = true;
			return new CursorSlot
			{
				Answered = true,
				Tile = tile,
				Entry = entry,
				Failure = ArmyActionFailureFlags.None,
				MoneyCost = FixedPoint.Zero,
				InfluenceCost = FixedPoint.Zero,
				UnitCost = FixedPoint.Zero
			};
		}
	}
}
