using Amplitude;
using Amplitude.Mercury.Data.Simulation;
using Amplitude.Mercury.Interop;
using Amplitude.Mercury.Simulation;
using SettlementPlacementAdvisor.composition;
using SettlementPlacementAdvisor.gameapi;
using SettlementPlacementAdvisor.presentation;

namespace SettlementPlacementAdvisor.pipeline
{
	/// <summary>
	///     The advisor chain: mode dispatch, the army-level gate, the pick computation, the cursor slot write,
	///     the publish.
	/// </summary>
	/// <remarks>
	///     <para>
	///         Writes happen only through the active mode's pipeline, into that mode's evaluation: the three
	///         wired modes share CampPlacementEvaluation, so a mode the mode-policy table has not wired writes
	///         nothing, and an activation scrubs the tail the previous mode's session left.
	///     </para>
	///     <para>
	///         An unaffordable tile stays recommended: the gate's money and influence failures are masked at
	///         admission, and no affordability prefilter runs anywhere in the pass.
	///     </para>
	///     <para>
	///         Bound: the configured-neighborhood scan (hovered territory plus RangeLevels - 1 adjacency
	///         hops; at the bind defaults a few hundred tiles, a few hundred gate calls) must stay
	///         within 5 ms per refresh on the net472 Unity Mono target.
	///         Never add a runtime cap, budget or affordability prefilter to meet it. No headless test measures the
	///         time, so a rising T1 counter (adjacency, gate, scoring or territory-fact reads per pass) is
	///         the drift signal: a scan spending more calls than the configured neighborhood accounts for
	///         is a scan going slower.
	///     </para>
	///     <para>
	///         BestCampPlacementTileIndexes keeps vanilla's -1 stamps: the mod's old suppression loop over it is
	///         gone, and no best-camp bookkeeping runs. The takeover-candidate disposition, including the candidates
	///         the rewrite rejected, is in the deleted spec: git show ecc7202:SPEC.md, section 1.
	///     </para>
	/// </remarks>
	internal sealed class Advisor
	{
		private readonly IGameSurface _mGame;
		private readonly PickComputation _mPicks;
		private readonly EvaluationWriter _mWriter;

		public Advisor(IGameSurface game, PickComputation picks, EvaluationWriter writer)
		{
			_mGame = game;
			_mPicks = picks;
			_mWriter = writer;
		}

		/// <summary>
		///     The sandbox start's reset: the pick cache goes empty, so the new game's first pass scans from
		///     scratch. It writes nothing, publishes nothing and touches no evaluation: the previous session's
		///     tail is the writer's to scrub, and a new game activates its own evaluation.
		/// </summary>
		public void ResetPicks()
		{
			_mPicks.ClearCache();
		}

		/// <summary>
		///     The snapshot pass: scan and publish only, no write. Any outcome that is not a pick set publishes
		///     inactive.
		/// </summary>
		/// <param name="evaluation">The placement evaluation this snapshot sync is filling.</param>
		/// <param name="cursorTile">The tile under the placement cursor.</param>
		/// <param name="armyGuid">The placement entity GUID vanilla resolves the placing Army from.</param>
		public void RunScanPass(CampPlacementEvaluation evaluation, int cursorTile, SimulationEntityGUID armyGuid)
		{
			(Pick[] picks, Outcome outcome, _) = Compute(evaluation, cursorTile, armyGuid);
			if (outcome != Outcome.Picks)
			{
				Deactivate(evaluation.SettleType);
				return;
			}

			Publish(picks, evaluation.SettleType, ArmyActionFailureFlags.None, PresentationView.NoCursorTile);
		}

		/// <summary>
		///     The refresh: scan, cursor slot write, publish. Zero picks publishes empty and scrubs the tail; a
		///     pass the surface cannot answer publishes inactive. No mode falls back on vanilla.
		/// </summary>
		/// <param name="evaluation">The placement evaluation this refresh writes.</param>
		/// <param name="cursorTile">The tile under the placement cursor.</param>
		/// <param name="armyGuid">The placement entity GUID vanilla resolves the placing Army from.</param>
		public void Run(CampPlacementEvaluation evaluation, int cursorTile, SimulationEntityGUID armyGuid)
		{
			(Pick[] picks, Outcome outcome, object army) = Compute(evaluation, cursorTile, armyGuid);
			ArmyActionType mode = evaluation.SettleType;
			switch (outcome)
			{
				case Outcome.Picks:
				{
					CursorSlot cursor = CursorSlotFacts(evaluation, army, cursorTile);
					_mWriter.Write(evaluation, picks, cursor);
					Publish(picks, mode, cursor.Failure, cursor.Tile);
					break;
				}
				case Outcome.NoPicks:
					PublishEmpty(evaluation);
					break;
				default:
					Deactivate(mode);
					break;
			}
		}

		/// <summary>
		///     The advisor owns a refresh only for a mode the mode-policy table wires.
		/// </summary>
		public static bool OwnsRefresh(CampPlacementEvaluation evaluation)
		{
			return ModePolicy.IsWired(evaluation.SettleType);
		}

		/// <summary>
		///     The cursor slot is the advisor's own entry: the hovered tile scored by an unmasked gate call,
		///     carrying SettleFailure plus the money, influence and unit costs.
		/// </summary>
		/// <remarks>
		///     <para>
		///         Its glyph follows vanilla's hovered rule alone, so a hovered tile that is also a pick is
		///         recolored by vanilla's own hover/advise kind switch and exactly one marker lands on every tile the
		///         cursor touches. An unanswered slot writes a scrubbed entry and leaves CurrentPositionIndex at -1.
		///     </para>
		/// </remarks>
		private CursorSlot CursorSlotFacts(CampPlacementEvaluation evaluation, object army, int cursorTile)
		{
			CursorSlot unanswered = CursorSlot.Unanswered;
			if (army == null)
			{
				return unanswered;
			}

			ArmyActionType mode = evaluation.SettleType;
			if (!_mGame.TryGetTileGateFailures(mode, army, cursorTile, out GateAnswer answer)
				|| !_mGame.TryGetDistrictDefinition(mode, out DistrictDefinition definition)
				|| !_mGame.EvaluateTile(army, cursorTile, definition, out DistrictPlacementEvaluation.ValidTile entry,
					out FixedPoint _))
			{
				return unanswered;
			}

			entry.Showable = HoveredRule.Allows(answer.Failures);

			return new CursorSlot
			{
				Answered = true,
				Tile = cursorTile,
				Entry = entry,
				Failure = answer.Failures,
				MoneyCost = answer.Money,
				InfluenceCost = answer.Influence,
				UnitCost = answer.Unit
			};
		}

		/// <summary>
		///     The fail-closed rows of a pass, in order: a mode the table has not wired, an army the surface
		///     cannot resolve, and a cursor tile no territory owns.
		/// </summary>
		/// <remarks>
		///     <para>
		///         The first is not advisable and publishes inactive; the rest publish empty and scrub the tail.
		///         The hovered territory is the territory owning the cursor tile: with none, the pass has no
		///         neighborhood at all.
		///     </para>
		/// </remarks>
		/// <returns>The pass's picks, its outcome, and the army it scanned for.</returns>
		private (Pick[] picks, Outcome outcome, object army) Compute(
			CampPlacementEvaluation evaluation,
			int cursorTile,
			SimulationEntityGUID armyGuid)
		{
			ArmyActionType mode = evaluation.SettleType;
			if (!ModePolicy.IsWired(mode))
			{
				return (null, Outcome.NotAdvisable, null);
			}

			object army = _mGame.TryGetArmy(armyGuid);
			if (army == null)
			{
				return (null, Outcome.NoPicks, null);
			}

			int hovered = _mGame.TerritoryOfTile(cursorTile);
			if (hovered < 0)
			{
				return (null, Outcome.NoPicks, army);
			}

			CursorContext context = CursorContext.For(hovered,
				army,
				_mGame.GetArmyEmpireIndex(army),
				SpaRuntime.TopHovered,
				SpaRuntime.TopNeighbour,
				SpaRuntime.RangeLevels,
				mode);
			Pick[] picks = _mPicks.Compute(context);
			Outcome outcome = picks == null || picks.Length == 0
								  ? Outcome.NoPicks
								  : Outcome.Picks;

			return (picks, outcome, army);
		}

		/// <summary>
		///     Publish the merged set ascending and unique by tile index. An unchanged set reuses the published
		///     instance, so equal publishes skip the cross-thread swap.
		/// </summary>
		private static void Publish(
			Pick[] picks,
			ArmyActionType mode,
			ArmyActionFailureFlags settleFailure,
			int cursorTile)
		{
			int[] tiles = new int[picks.Length];
			for (int i = 0; i < picks.Length; i++)
			{
				tiles[i] = picks[i].Tile;
			}

			PresentationView.Publish(tiles, mode, settleFailure, cursorTile);
		}

		private static void Deactivate(ArmyActionType mode)
		{
			PresentationView.PublishInactive(mode);
		}

		/// <summary>
		///     Zero picks: scrub the tail the previous refresh wrote, then publish inactive, so no white hex
		///     survives an empty pass.
		/// </summary>
		public void PublishEmpty(CampPlacementEvaluation evaluation)
		{
			_mWriter.ClearTail(evaluation);
			Deactivate(evaluation.SettleType);
		}

		private enum Outcome
		{
			NotAdvisable,
			NoPicks,
			Picks
		}
	}
}
