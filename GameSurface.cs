using Amplitude; // FixedPoint (assembly Amplitude.Framework.dll)
using Amplitude.Mercury.Data.Simulation; // ArmyActionType
using Amplitude.Mercury.Interop;
using Amplitude.Mercury.Simulation; // SimulationEntityGUID
using SnapshotTerritory = Amplitude.Mercury.Interop.AI.Entities.Territory; // snapshot territory edges

namespace SettlementPlacementAdvisor
{
    /// <summary>
    /// Snapshot territory facts one territory filter reads, taken from the snapshot
    /// TerritoryInfo record. Read-only, valid for the current refresh, not a retention.
    /// The tiles view is the territory's settlement-site tiles.
    /// </summary>
    internal struct TerritoryFacts
    {
        /// <summary>Map-edge territory: vanilla never claims it (ArmyActionHelper.cs:510-514).</summary>
        public bool IsEdgeOfTheWorld;

        /// <summary>A settlement claims the territory (TerritoryInfo.Claimed).</summary>
        public bool Claimed;

        /// <summary>A settlement sits on it (TerritoryInfo.SettlementGUID != 0): settlement site.</summary>
        public bool HasSettlement;

        /// <summary>Owning empire (TerritoryInfo.EmpireIndex), -1 when unowned.</summary>
        public int OwnerEmpireIndex;

        /// <summary>Settlement-site tiles of the territory.</summary>
        public int[] Tiles;
    }

    /// <summary>
    /// Outcome of a territory-facts read. Unknown is a distinct outcome, not a shape of a
    /// known territory: it fails closed and never advises.
    /// </summary>
    internal enum TerritoryFactsResult
    {
        Unknown,
        Known,
    }

    /// <summary>
    /// All game facts the advisor needs, as one seam over GameApi. Failure semantics are part
    /// of the contract: IsSettlementSiteTile and territory facts fail closed, IsTileExplored fails
    /// open, TryGetArmy returns null, frame counters return false, and the gate/definition
    /// queries return false when the fact cannot be answered. Rule modules never touch
    /// the game's AI snapshot globals directly.
    /// </summary>
    internal interface GameSurface
    {
        /// <summary>False after a failed Resolve: the advisor stays off and vanilla is untouched.</summary>
        bool Ready { get; }

        /// <summary>Simulation-side Army entity for the GUID, or null (sim thread).</summary>
        object TryGetArmy(SimulationEntityGUID guid);

        /// <summary>Owning empire of an army entity, -1 when the army is null.</summary>
        int GetArmyEmpireIndex(object army);

        /// <summary>
        /// The district definition the given placement mode evaluates with (CampDefinition for
        /// CreateCamp, CityCenterDefinition for both city modes). False for a mode the surface
        /// does not map. The settler's starting pack is deliberately absent: the vanilla
        /// evaluator never reads it, so it never reaches scoring.
        /// </summary>
        bool TryGetDistrictDefinition(ArmyActionType mode, out object districtDefinition);

        /// <summary>
        /// Terrain + point-of-interest validity for a settlement site on the tile, against the
        /// mode's district definition (sim thread). Fail closed: false when unknown.
        /// </summary>
        bool IsSettlementSiteTile(object districtDefinition, int tileIndex);

        /// <summary>Water tile (fail closed: true when unknown). Sea-flagged territories can
        /// host valid camp sites, so water is filtered per tile, never by the territory's
        /// ocean flag (observed: a sea-flagged territory held all 25 valid camp sites).</summary>
        bool IsTileWater(int tileIndex);

        /// <summary>
        /// Resource tile: the tile's point of interest resolves to a resource deposit, the fact
        /// vanilla records as ValidTile.Resource (SimulationEvaluator.cs:2691-2694) and that its
        /// settlement scoring excludes (2399), leaving the tile to an extractor. Fail closed:
        /// true when the point of interest cannot be resolved.
        /// </summary>
        bool IsTileResourceTile(int tileIndex);

        /// <summary>
        /// Tile scouted by the given empire, as the gate's own predicate
        /// ((ExplorationMap | BattleExplorationMap) &amp; bits) != 0 (VisibilityController.cs:255-258):
        /// any shared vision bit counts. Cache invalidation via VisibilityFrame.
        /// </summary>
        bool IsTileExplored(int tileIndex, int empireIndex);

        /// <summary>
        /// Full placement evaluation for one tile (sim thread): fills the ValidTile
        /// (FIMS, exploitation tiles, build time) and returns the weighted score using
        /// the game's own settlementScorePonderation. Scores with the mode's district
        /// definition; a settlement starting pack is never an input.
        /// </summary>
        bool EvaluateTile(object army, int tileIndex, object districtDefinition, out DistrictPlacementEvaluation.ValidTile validTile, out FixedPoint score);

        /// <summary>World frame counters used to invalidate the per-territory pick cache.</summary>
        bool GetCurrentFrames(out int districtFrame, out int constructionFrame, out int visibilityFrame);

        /// <summary>
        /// World.TerritoryInfo.Frame: the claim/exploration frame, bumped by claims, unclaims,
        /// owner steals, exploration and patrol changes (World.cs:870,2358,2383,2399,2780,2820).
        /// Cache key input; false when the counter is unreadable.
        /// </summary>
        bool GetTerritoryFrame(out int territoryFrame);

        /// <summary>Territory owning the tile, -1 when the tile has no territory (sim thread).</summary>
        int TerritoryOfTile(int tileIndex);

        /// <summary>
        /// Snapshot territory facts (sim thread) for the mode's territory filter. Unknown fails
        /// closed: never advise an unknown territory. No ocean flag: it is territory metadata
        /// that misfires on sea-flagged territories hosting land camp sites; see IsTileWater.
        /// </summary>
        TerritoryFactsResult GetTerritoryFacts(int territoryIndex, out TerritoryFacts facts);

        /// <summary>
        /// Adjacency edges of a territory, in snapshot order, ALL of them: sea, cliff, mountain
        /// and closed edges included, no transition filter here. Callers pick the edges their
        /// rule steps over. Empty when unknown. Snapshot view: read-only, valid for the
        /// current refresh.
        /// </summary>
        SnapshotTerritory.Adjacency[] AdjacentTerritories(int territoryIndex);

        /// <summary>
        /// The mode's tile gate (sim thread, one reflection call per tile): the settle-cost body
        /// stays intact. maskMoneyAndInfluence applies the membership mask
        /// (GameApi.ApplyMoneyInfluenceMask); the unmasked form keeps the money/influence
        /// failures, which is what the cursor slot writes. False when the gate cannot be
        /// answered (unmapped mode, missing army, reflection failure).
        /// </summary>
        bool TryGetTileGateFailures(ArmyActionType mode, object army, int tileIndex, bool maskMoneyAndInfluence, out ArmyActionFailureFlags failures, out FixedPoint moneyCost, out FixedPoint influenceCost, out FixedPoint unitCost);

        /// <summary>
        /// The mode's army-level action gate (army overload, computeMinimumCost false): the
        /// facts about the army itself, no target tile, no cheapest-claim cost math.
        /// False when the gate cannot be answered.
        /// </summary>
        bool TryGetArmyGateFailures(ArmyActionType mode, object army, out ArmyActionFailureFlags failures);
    }
}
