using Amplitude.Mercury.Data.Simulation;
using Amplitude.Mercury.Interop;
using SettlementPlacementAdvisor.logger;
using SettlementPlacementAdvisor.pipeline;

namespace SettlementPlacementAdvisor.presentation
{
	/// <summary>
	///     PresentationView.Current is the advisor's only cross-thread channel: one volatile reference swap
	///     carrying the pick set, the mode it was published for, the cursor tile, and its gate answer.
	///     Presentation code reads only that captured view. There is no other shared mutable state, and no Unity
	///     objects on the simulation thread.
	/// </summary>
	/// <remarks>
	///     <para>
	///         Every member gets vanilla's own advised-tile overlay in all three modes: the writer's Showable
	///         entries are vanilla-evaluated with FimsTier left at 0, so vanilla draws its white advised hex on
	///         exactly the pick set. No overlay cap, no tie cap, and no advisor-owned fx. One presentation contract in
	///         all three modes; mode-specific vanilla surfaces stay mode-only.
	///     </para>
	///     <para>
	///         Inactive displays nothing in any mode: no pins, no cursor entry, no glyphs. The advised-tile
	///         overlay is vanilla's own and clears with vanilla's cursor deactivation.
	///     </para>
	/// </remarks>
	internal sealed class PresentationView
	{
		public const int NoCursorTile = -1;

		/// <summary>
		///     The swap point: written by the simulation thread, read by the presentation thread.
		/// </summary>
		public static volatile PresentationView Current = Boot;

		public readonly int CursorTile;
		public readonly ArmyActionType Mode;
		public readonly ArmyActionFailureFlags SettleFailure;

		private readonly PickSet _picks;

		private PresentationView(
			PickSet picks,
			ArmyActionType mode,
			ArmyActionFailureFlags settleFailure,
			int cursorTile)
		{
			_picks = picks;
			Mode = mode;
			SettleFailure = settleFailure;
			CursorTile = cursorTile;
		}

		private static PresentationView Boot => new PresentationView(null, ArmyActionType.AttackArmy,
			ArmyActionFailureFlags.None, NoCursorTile);

		/// <summary>
		///     Inactive is a published view with no pick set.
		/// </summary>
		public bool IsActive => _picks?.Tiles != null;

		/// <summary>
		///     Pin-gate membership on the ascending tiles this view published.
		/// </summary>
		/// <returns>true when the tile is in this view's pick set.</returns>
		public bool Contains(int tileIndex)
		{
			PickSet picks = _picks;
			return picks != null && picks.Contains(tileIndex);
		}

		/// <summary>
		///     Publish one view. An unchanged pick set reuses the published instance, so equal publishes skip the
		///     swap.
		/// </summary>
		/// <param name="tiles">The pick set ascending by tile index; null publishes inactive.</param>
		/// <param name="mode">The placement mode the pick set was computed for.</param>
		/// <param name="settleFailure">The cursor slot's unmasked gate answer.</param>
		/// <param name="cursorTile">The tile the cursor sat on, or NoCursorTile.</param>
		public static void Publish(
			int[] tiles,
			ArmyActionType mode,
			ArmyActionFailureFlags settleFailure,
			int cursorTile)
		{
			PresentationView current = Current;
			PickSet picks = PickSet.Build(tiles, current?._picks);
			if (current != null
				&& current._picks == picks
				&& current.Mode == mode
				&& current.SettleFailure == settleFailure
				&& current.CursorTile == cursorTile)
			{
				return;
			}

			Current = new PresentationView(picks, mode, settleFailure, cursorTile);

			if (picks != current?._picks)
			{
				SpaLogger.Log("publish picks=" + (tiles?.Length ?? 0));
			}
		}

		/// <summary>
		///     Zero picks, every fail-closed outcome, and the cursor's deactivation publish inactive.
		/// </summary>
		public static void PublishInactive(ArmyActionType mode)
		{
			Publish(null, mode, ArmyActionFailureFlags.None, NoCursorTile);
		}
	}
}
