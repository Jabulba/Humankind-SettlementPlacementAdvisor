using SettlementPlacementAdvisor.gameapi;
using SettlementPlacementAdvisor.pipeline;

namespace SettlementPlacementAdvisor.composition
{
	/// <summary>
	///     The composition hand-off: Plugin.Awake fills these instances and the config snapshot on the
	///     load thread before the patches are applied, and every later read is read-only, the patch classes
	///     and the headless tests alike. Nothing replaces them mid-game.
	/// </summary>
	/// <remarks>
	///     <para>
	///         These fields are not a shared-state channel: the advisor's one cross-thread channel is
	///         PresentationView.Current.
	///     </para>
	/// </remarks>
	internal static class SpaRuntime
	{
		internal static IGameSurface GameApi;

		internal static Advisor Advisor;

		internal static EvaluationWriter Writer;

		/// <summary>
		///     The bound config snapshot: the values AdvisorConfig.Bind returned, taken once in
		///     Plugin.Awake. The scan and the writer's tail reserve read these, never a ConfigEntry.
		/// </summary>
		internal static int TopHovered;

		internal static int TopNeighbour;
		internal static int RangeLevels;

		/// <summary>
		///     AdvisorConfig.TailCapacityFor of the snapshot, taken in the same Awake.
		/// </summary>
		internal static int TailCapacity;
	}
}
