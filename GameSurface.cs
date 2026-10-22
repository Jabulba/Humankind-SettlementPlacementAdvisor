using Amplitude; // FixedPoint (assembly Amplitude.Framework.dll)
using Amplitude.Mercury.Interop;
using Amplitude.Mercury.Simulation; // SimulationEntityGUID

namespace SettlementPlacementAdvisor
{
    /// <summary>
    /// THE GAME SURFACE: every game fact the advisor needs to compute a pick, as one seam.
    /// The adapter GameApi resolves the game members once by reflection at plugin start; from
    /// then on the pick computation and the advisor talk to this interface only. Failure
    /// semantics are part of the contract: IsCampableTile fails closed (never recommend an
    /// unchecked tile), IsTileExplored fails open, TryGetArmy returns null, frame counters
    /// return false. The territory graph lives here too: the rule modules never touch the AI
    /// snapshot globals, and territory facts fail closed (an unknown territory never advises).
    /// </summary>
    internal interface GameSurface
    {
        /// <summary>False after a failed Resolve: the advisor stays off and vanilla is untouched.</summary>
        bool Ready { get; }

        /// <summary>Simulation-side Army entity for the GUID, or null (sim thread).</summary>
        object TryGetArmy(SimulationEntityGUID guid);

        /// <summary>Owning empire of an army entity, -1 when the army is null.</summary>
        int GetArmyEmpireIndex(object army);

        /// <summary>Land + point-of-interest validity for a camp site on the tile (sim thread).</summary>
        bool IsCampableTile(int tileIndex);

        /// <summary>Tile scouted by the given empire (cache invalidation via VisibilityFrame).</summary>
        bool IsTileExplored(int tileIndex, int empireIndex);

        /// <summary>
        /// Full placement evaluation for one tile (sim thread): fills the ValidTile
        /// (FIMS, exploitation tiles, build time) and returns the weighted score using
        /// the game's own settlementScorePonderation.
        /// </summary>
        bool EvaluateTile(object army, int tileIndex, out DistrictPlacementEvaluation.ValidTile validTile, out FixedPoint score);

        /// <summary>World frame counters used to invalidate the per-territory pick cache.</summary>
        bool GetCurrentFrames(out int districtFrame, out int constructionFrame, out int visibilityFrame);

        /// <summary>Territory owning the tile, -1 when the tile has no territory (sim thread).</summary>
        int TerritoryOfTile(int tileIndex);

        /// <summary>
        /// Territory facts (sim thread): ocean flag, claimed flag, camp-site tiles. False =
        /// territory unknown (fail closed: never advise an unknown territory). The tiles view
        /// belongs to the snapshot: read-only, valid for the current refresh, not a retention.
        /// </summary>
        bool GetTerritoryFacts(int territoryIndex, out bool isOcean, out bool isClaimed, out int[] tiles);

        /// <summary>
        /// Land-adjacent territory indexes: only the edges a ground army crosses
        /// (TerritoryTransition.Open). Water, cliff, mountain and closed edges never
        /// count as a range step. Empty when unknown. Fresh array the caller may keep.
        /// </summary>
        int[] LandAdjacentTerritories(int territoryIndex);
    }
}
