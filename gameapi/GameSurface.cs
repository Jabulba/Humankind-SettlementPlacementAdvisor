using System.Reflection;
using Amplitude;
using Amplitude.Mercury.Data.Simulation;
using Amplitude.Mercury.Interop;
using Amplitude.Mercury.Simulation;
using SnapshotTerritory = Amplitude.Mercury.Interop.AI.Entities.Territory;

// The rules' seam over the game's internals: tile, exploration, evaluation, army and territory-graph facts,
// the patch binding, the snapshot cursor facts, and the evaluation activation answer, all on the same seam.
// No pipeline module reaches past IGameSurface into the adapter's binding table, its labels, or its compiled
// delegates; GameApi satisfies the interface and keeps the marshaling to itself.
//
// Each test's fail-open/fail-closed polarity lives in the surface, not at the call site: the pre-filter
// answers one verdict per tile, an unanswerable tile gate drops its tile, and an unanswerable army-level
// gate or frame drops the whole pass.

namespace SettlementPlacementAdvisor.gameapi
{
	/// <summary>
	///     The territory facts a mode's territory filter reads and the pick computation caches: the filter
	///     inputs, the owning empire, and the territory's tiles.
	/// </summary>
	/// <remarks>
	///     <para>
	///         Territory facts fail closed: an unknown territory never advises, it contributes no tile and
	///         asks no gate.
	///     </para>
	/// </remarks>
	internal struct TerritoryFacts
	{
		public bool IsEdgeOfTheWorld;

		public bool Claimed;

		public bool HasSettlement;

		public bool IsOwnedByCity;

		public int OwnerEmpireIndex;

		public int[] Tiles;
	}

	/// <summary>
	///     Known answers the filter; Unknown closes the territory.
	/// </summary>
	internal enum TerritoryFactsResult
	{
		Unknown,
		Known
	}

	/// <summary>
	///     The Harmony targets, named by label through the binding pass: the mode dispatch and refresh prefixes,
	///     the activation and deactivation postfixes, the pin gate, and the sandbox start's cache reset.
	/// </summary>
	internal enum PatchPoint
	{
		EvaluatePlacement,
		UpdateCurrentPosition,
		EvaluationInitialize,
		DeactivateCursor,
		PinMustMeShown,
		SandboxStart
	}

	/// <summary>
	///     The tile pre-filter's single verdict: water per tile, explored with the gate's any-bit predicate,
	///     terrain and POI with the mode's district definition. Never filter on IsOcean or AnyClaimableTile.
	/// </summary>
	internal enum TileVerdict
	{
		Drop,
		Pass
	}

	/// <summary>
	///     The tile gate's unmasked answer: failure flags, plus the money, influence and unit costs the camp
	///     gate reports. The city and settler gates report flags only. The pick computation applies the
	///     membership mask.
	/// </summary>
	internal struct GateAnswer
	{
		public ArmyActionFailureFlags Failures;
		public FixedPoint Money;
		public FixedPoint Influence;
		public FixedPoint Unit;
	}

	/// <summary>
	///     The seam the rules read. Answers are unmasked: filtering is the rules' job, not the surface's, so the
	///     same gate answers both the scan and the cursor slot.
	/// </summary>
	internal interface IGameSurface
	{
		MethodBase PatchTarget(PatchPoint point);

		bool TryReadSnapshotActionType(SettlementPlacementCursorSnapshot snapshot, out ArmyActionType mode);

		int ReadSnapshotCursorTile(SettlementPlacementCursorSnapshot snapshot);

		bool TryActivateEvaluation(CampPlacementEvaluation evaluation);

		object TryGetArmy(SimulationEntityGUID guid);

		int GetArmyEmpireIndex(object army);

		bool TryGetDistrictDefinition(ArmyActionType mode, out DistrictDefinition districtDefinition);

		TileVerdict TestTilePrefilter(int tileIndex, int empireIndex, DistrictDefinition districtDefinition);

		bool IsTileResourceTile(int tileIndex);

		bool EvaluateTile(
			object army,
			int tileIndex,
			DistrictDefinition districtDefinition,
			out DistrictPlacementEvaluation.ValidTile validTile,
			out FixedPoint score);

		bool GetCurrentFrames(out int districtFrame, out int constructionFrame, out int visibilityFrame);

		bool GetTerritoryFrame(out int territoryFrame);

		int TerritoryOfTile(int tileIndex);

		TerritoryFactsResult GetTerritoryFacts(int territoryIndex, out TerritoryFacts facts);

		bool TryGetAdjacentTerritories(int territoryIndex, out SnapshotTerritory.Adjacency[] edges);

		bool TryGetTileGateFailures(ArmyActionType mode, object army, int tileIndex, out GateAnswer answer);

		bool TryGetArmyGateFailures(ArmyActionType mode, object army, out ArmyActionFailureFlags failures);
	}
}
