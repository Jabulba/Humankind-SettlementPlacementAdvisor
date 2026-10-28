using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Amplitude;
using Amplitude.Mercury.Interop;
using SettlementPlacementAdvisor.composition;
using SettlementPlacementAdvisor.gameapi;
using SettlementPlacementAdvisor.logger;

namespace SettlementPlacementAdvisor.pipeline
{
	/// <summary>
	///     The advisor-owned cursor slot: the hovered tile's entry, its unmasked gate answer, and the money,
	///     influence and unit costs that gate reports. Only the camp gate answers costs; the city and settler
	///     gates report flags only.
	/// </summary>
	internal struct CursorSlot
	{
		public bool Answered;
		public int Tile;
		public DistrictPlacementEvaluation.ValidTile Entry;
		public ArmyActionFailureFlags Failure;
		public FixedPoint MoneyCost;
		public FixedPoint InfluenceCost;
		public FixedPoint UnitCost;

		public static readonly CursorSlot Unanswered = new CursorSlot();
	}

	/// <summary>
	///     Vanilla's rule for the tile under the cursor: its pin and glyph show unless the gate answer reports a
	///     failure other than insufficient influence. Cost failure surfaces on the hovered tile only, so an
	///     unaffordable pick keeps its marker and pin.
	/// </summary>
	internal static class HoveredRule
	{
		public static bool Allows(ArmyActionFailureFlags settleFailure)
		{
			return settleFailure == ArmyActionFailureFlags.None
				   || settleFailure == ArmyActionFailureFlags_Part1.NotEnoughInfluence;
		}
	}

	/// <summary>
	///     The evaluation writer puts picks into the running mode's evaluation and owns that evaluation's tail
	///     window in the shared ValidTiles array. Writes happen only through the active mode's pipeline, into
	///     that mode's evaluation: a mode the mode-policy table has not wired writes nothing.
	/// </summary>
	/// <remarks>
	///     <list type="number">
	///         <item>
	///             <description>
	///                 Tail capacity is the config snapshot's bound in SpaRuntime (the worst-case
	///                 admission set for the bound knobs, AdvisorConfig.TailCapacityFor). The window is
	///                 reserved at activation, grows on demand, and a release scrubs the whole region
	///                 ever reserved.
	///             </description>
	///         </item>
	///         <item>
	///             <description>
	///                 Pick slots are written in place at stable indices, then a barrier, then
	///                 ValidTileCount rises. Vanilla entries never move, because vanilla pins bind to array
	///                 indices.
	///             </description>
	///         </item>
	///         <item>
	///             <description>
	///                 Zero-fill is incremental: only the slots a refresh releases are zeroed, never a
	///                 sweep of the array, and Showable equals pick-set membership.
	///             </description>
	///         </item>
	///         <item>
	///             <description>
	///                 A mode switch scrubs the previous mode's tail over the whole region, never merely
	///                 counts down.
	///             </description>
	///         </item>
	///         <item>
	///             <description>
	///                 ConditionalFlagsTileIndexes is written on the same indices as ValidTiles and grows
	///                 with it: every row the writer owns, cursor slot and pick slots alike, is answered
	///                 DistrictPlacementConditionFlags.None, and the rows below the tail, which are vanilla's, are never
	///                 touched. This is a presentation choice, not a passthrough: vanilla's hovered cursor reads the cursor
	///                 slot through GetCurrentPositionConditionFlags, so the advisor tells it the hovered tile carries no
	///                 district condition.
	///             </description>
	///         </item>
	///     </list>
	///     <para>
	///         Overlay timing follows the list: a scan pass publishes without writing, so white hexes appear
	///         from the first refresh write onward, the same list-driven cadence vanilla's own recommendation
	///         surfaces use.
	///     </para>
	///     <para>
	///         Window records are keyed weakly on the evaluation: the game creates an evaluation per placement
	///         session and drops it when the session ends, and the writer's record for it goes with it, so an
	///         ended session leaves no writer state behind.
	///     </para>
	/// </remarks>
	internal sealed class EvaluationWriter
	{
		private static readonly Pick[] SEmpty = Array.Empty<Pick>();

		private readonly IGameSurface _mGame;

		// Weak on purpose: the game creates an evaluation per placement session and drops it when
		// the session ends, and the writer's record for it must die with it. A strong table would pin
		// every evaluation the advisor ever wrote, arrays included.
		private readonly ConditionalWeakTable<CampPlacementEvaluation, TailWindow> _mWindows =
			new ConditionalWeakTable<CampPlacementEvaluation, TailWindow>();

		public EvaluationWriter(IGameSurface game)
		{
			_mGame = game;
		}

		/// <summary>
		///     Activation tail reserve: release the previous session's tail, then reserve the window at the
		///     current ValidTileCount. A mode the table has not wired reserves nothing, so an activation scrubs
		///     the tail the previous mode's session left and writes nothing.
		/// </summary>
		public void NoteActivation(CampPlacementEvaluation evaluation)
		{
			ReleaseTail(evaluation);

			if (!ModePolicy.IsWired(evaluation.SettleType))
			{
				return;
			}

			TailWindow window = Reserve(evaluation, evaluation.ValidTileCount);

			SpaLogger.Log("activation baseline count=" + window.TailStart + " reserved=" + window.ReservedEnd);
		}

		/// <summary>
		///     The snapshot takeover's activation step: the game surface's activation answer decides, then the
		///     reserve runs.
		/// </summary>
		public void ReserveBaseline(CampPlacementEvaluation evaluation)
		{
			if (!_mGame.TryActivateEvaluation(evaluation))
			{
				return;
			}

			NoteActivation(evaluation);
		}

		/// <summary>
		///     The refresh write: the cursor slot at the window start, the pick slots in place after it, Showable
		///     set to pick-set membership, then the barrier, then ValidTileCount. Only the slots this refresh
		///     releases are zeroed, and the rows below the tail stay vanilla's.
		/// </summary>
		/// <param name="evaluation">The evaluation whose tail the writer owns.</param>
		/// <param name="picks">The published pick set, in ascending tile order.</param>
		/// <param name="cursor">The hovered tile's scored slot, or an unanswered slot.</param>
		public void Write(CampPlacementEvaluation evaluation, Pick[] picks, CursorSlot cursor)
		{
			if (!ModePolicy.IsWired(evaluation.SettleType))
			{
				return;
			}

			TailWindow window = WindowFor(evaluation);
			if (window.TailStart < 0)
			{
				Reserve(evaluation,
					evaluation.ValidTileCount);
			}

			int count = evaluation.ValidTileCount;
			int tailEnd = window.TailStart + 1 + picks.Length;
			int needed = Math.Max(count, tailEnd);
			if (needed > window.ReservedEnd)
			{
				Grow(window, evaluation, needed);
			}

			DistrictPlacementEvaluation.ValidTile[] tiles = evaluation.ValidTiles;
			DistrictPlacementConditionFlags[] flags = evaluation.ConditionalFlagsTileIndexes;

			DistrictPlacementEvaluation.ValidTile slot = cursor.Answered
															 ? cursor.Entry
															 : Scrubbed();
			if (!cursor.Answered)
			{
				slot.Showable
					= false;
			}

			tiles[window.TailStart] = slot;
			if (flags != null)
			{
				flags[window.TailStart] = DistrictPlacementConditionFlags.None;
			}

			evaluation.SettleFailure = cursor.Failure;
			evaluation.InstantMoneyCost = cursor.MoneyCost;
			evaluation.InstantInfluenceCost = cursor.InfluenceCost;
			evaluation.InstantUnitCost = cursor.UnitCost;

			for (int i = 0; i < picks.Length; i++)
			{
				DistrictPlacementEvaluation.ValidTile entry = picks[i].Data;
				entry.Showable = true;
				tiles[window.TailStart + 1 + i] = entry;
				if (flags != null)
				{
					flags[window.TailStart + 1 + i] = DistrictPlacementConditionFlags.None;
				}
			}

			ZeroFill(evaluation, tailEnd, window.WrittenEnd);
			window.WrittenEnd = tailEnd;

			Thread.MemoryBarrier();

			evaluation.CurrentPositionIndex = cursor.Answered
												  ? window.TailStart
												  : -1;
			evaluation.ValidTileCount = needed;
		}

		/// <summary>
		///     The empty pass: a write with no picks and no cursor slot scrubs the tail, so no white hex survives.
		/// </summary>
		public void ClearTail(CampPlacementEvaluation evaluation)
		{
			Write(evaluation, SEmpty, CursorSlot.Unanswered);
		}

		/// <summary>
		///     Reserve: the window starts at the activation's count, the region spans TailCapacity rows, and the
		///     reserved region is scrubbed on the way in.
		/// </summary>
		/// <returns>The evaluation's tail window.</returns>
		private TailWindow Reserve(CampPlacementEvaluation evaluation, int baseline)
		{
			TailWindow window = WindowFor(evaluation);
			window.TailStart = baseline;
			window.WrittenEnd = baseline + 1;
			int regionEnd = baseline + 1 + SpaRuntime.TailCapacity;
			if (regionEnd > window.ReservedEnd)
			{
				window.ReservedEnd = regionEnd;
			}

			EnsureCapacity(evaluation, window.ReservedEnd);
			ZeroFill(evaluation, baseline, window.ReservedEnd);
			return window;
		}

		/// <summary>
		///     The window grows on demand when a refresh needs more rows than the reserved region has.
		/// </summary>
		private void Grow(TailWindow window, CampPlacementEvaluation evaluation, int regionEnd)
		{
			window.ReservedEnd = regionEnd;
			EnsureCapacity(evaluation, regionEnd);
			SpaLogger.Log("tail region grown to " + regionEnd);
		}

		/// <summary>
		///     The arrays grow with the region, and the rows they newly cover are scrubbed, so a grown row never
		///     carries a stale entry.
		/// </summary>
		private static void EnsureCapacity(CampPlacementEvaluation evaluation, int regionEnd)
		{
			DistrictPlacementEvaluation.ValidTile[] tiles = evaluation.ValidTiles;
			int grown = tiles == null
							? 0
							: tiles.Length;
			if (tiles == null || tiles.Length < regionEnd)
			{
				Array.Resize(ref tiles, regionEnd);
				evaluation.ValidTiles = tiles;
			}

			DistrictPlacementConditionFlags[] flags = evaluation.ConditionalFlagsTileIndexes;
			if (flags != null && flags.Length < regionEnd)
			{
				Array.Resize(ref flags, regionEnd);
				evaluation.ConditionalFlagsTileIndexes = flags;
			}

			if (grown < regionEnd)
			{
				ZeroFill(evaluation, grown, regionEnd);
			}
		}

		/// <summary>
		///     A release scrubs the whole region ever reserved, so a mode switch leaves no row of the previous
		///     mode's session, and resets the window record to unreserved.
		/// </summary>
		private void ReleaseTail(CampPlacementEvaluation evaluation)
		{
			TailWindow window;
			if (!_mWindows.TryGetValue(evaluation, out window) || window.TailStart < 0)
			{
				return;
			}

			ZeroFill(evaluation, window.TailStart, window.ReservedEnd);

			window.TailStart = -1;
			window.ReservedEnd = 0;
			window.WrittenEnd = 0;
		}

		private TailWindow WindowFor(CampPlacementEvaluation evaluation)
		{
			TailWindow window;
			if (!_mWindows.TryGetValue(evaluation, out window))
			{
				window = new TailWindow();
				_mWindows.Add(evaluation, window);
			}

			return window;
		}

		/// <summary>
		///     A scrubbed row: tile -1, no glyph.
		/// </summary>
		private static DistrictPlacementEvaluation.ValidTile Scrubbed()
		{
			DistrictPlacementEvaluation.ValidTile cleared = default;
			cleared.Initialize(-1);
			return cleared;
		}

		/// <summary>
		///     Zero-fill over a range clipped to the arrays: a scrubbed row on each ValidTiles index, and
		///     DistrictPlacementConditionFlags.None on each flags index.
		/// </summary>
		private static void ZeroFill(CampPlacementEvaluation evaluation, int from, int to)
		{
			DistrictPlacementEvaluation.ValidTile[] tiles = evaluation.ValidTiles;
			if (tiles != null)
			{
				int end = Math.Min(tiles.Length, to);
				for (int i = Math.Max(from, 0); i < end; i++)
				{
					tiles[i] = Scrubbed();
				}
			}

			DistrictPlacementConditionFlags[] flags = evaluation.ConditionalFlagsTileIndexes;
			if (flags != null)
			{
				int end = Math.Min(flags.Length, to);
				for (int i = Math.Max(from, 0); i < end; i++)
				{
					flags[i] = DistrictPlacementConditionFlags.None;
				}
			}
		}

		/// <summary>
		///     The writer's tail window: where it starts, where the region ever reserved ends, and where the last
		///     refresh wrote.
		/// </summary>
		private sealed class TailWindow
		{
			public int ReservedEnd;
			public int TailStart = -1;
			public int WrittenEnd;
		}
	}
}
