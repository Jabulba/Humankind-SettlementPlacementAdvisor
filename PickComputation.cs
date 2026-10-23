using System;
using System.Collections.Generic;
using Amplitude; // FixedPoint (namespace Amplitude, assembly Amplitude.Framework.dll).
using Amplitude.Mercury.Data.Simulation; // ArmyActionType.
using Amplitude.Mercury.Interop;
using Amplitude.Mercury.Simulation;

namespace SettlementPlacementAdvisor
{
    /// <summary>One pick: tile, weighted score, and vanilla's evaluation record for it.</summary>
    internal struct Pick
    {
        public int Tile;
        public FixedPoint Score;
        public DistrictPlacementEvaluation.ValidTile Data;
    }

    /// <summary>
    /// All input for one refresh. Count clamping (minimum 1: counts are mandatory) lives
    /// here, not in the caller.
    /// </summary>
    internal struct CursorContext
    {
        public int HoveredTerritory;
        public object Army;
        public int EmpireIndex;
        public int TopHovered;
        public int TopNeighbour;
        public int RangeLevels;
        public ArmyActionType Mode;

        public static CursorContext For(int hoveredTerritory, object army, int empireIndex,
            int topHovered, int topNeighbour, int rangeLevels, ArmyActionType mode)
        {
            return new CursorContext
            {
                HoveredTerritory = hoveredTerritory,
                Army = army,
                EmpireIndex = empireIndex,
                TopHovered = Math.Max(1, topHovered),
                TopNeighbour = Math.Max(1, topNeighbour),
                RangeLevels = Math.Max(1, rangeLevels),
                Mode = mode,
            };
        }
    }

    /// <summary>
    /// The mode-policy table (SPEC section 3): which mode the pipeline runs, and the territory
    /// filter that mode evaluates. A mode the table has not wired fails closed: the advisor
    /// advises nothing and vanilla stays in charge.
    /// </summary>
    internal static class ModePolicy
    {
        /// <summary>The wired rows of the table. Camp only in this slice; the city rows come
        /// with the city modes, and anything else fails closed.</summary>
        public static bool IsWired(ArmyActionType mode)
        {
            return mode == ArmyActionType.CreateCamp;
        }

        /// <summary>
        /// Claimable territory for the mode. Camp row: drop IsEdgeOfTheWorld
        /// (ArmyActionHelper.cs:510-514) and drop (Claimed || SettlementGUID != 0) (515-518).
        /// Never a filter: the territory ocean flag, AnyClaimableTile, DemilitarizedByEmpireIndex,
        /// close-border bits.
        /// </summary>
        public static bool IsTerritoryClaimable(ArmyActionType mode, TerritoryFacts facts)
        {
            return !facts.IsEdgeOfTheWorld && !facts.Claimed && !facts.HasSettlement;
        }
    }

    /// <summary>
    /// Which tiles to advise for a cursor context. Pure: reads the game only through
    /// GameSurface and returns ascending, unique picks (null = game facts unavailable).
    ///
    /// Pipeline per scan pass (SPEC section 2): mode dispatch, the army-level gate once per pass,
    /// then per territory the mode's territory filter, the tile pre-filter (water, explored with
    /// the gate's any-bit predicate, terrain + POI with the mode's district definition), the mode's
    /// tile gate with the money/influence mask, scoring on the vanilla weight table with the
    /// resource-tile exclusion kept, and admission as the clamped quota plus the full top tie set.
    ///
    /// Deliberately narrow in this slice: the scan covers the hovered territory only; the
    /// placement-neighborhood scan is the next slice. The per-territory cache is keyed by
    /// territory, top count, empire, placement mode, and the frames read here (including the
    /// world claim/exploration frame); callers never thread frames. Rules: README.md.
    /// </summary>
    internal sealed class PickComputation
    {
        /// <summary>One scan pass's read-only inputs, threaded to the per-territory work.</summary>
        private struct Pass
        {
            public CursorContext Context;
            public object DistrictDefinition;
            public int DistrictFrame;
            public int ConstructionFrame;
            public int VisibilityFrame;
            public int TerritoryFrame;
        }

        private struct CacheEntry
        {
            public Pick[] Picks;
            public int TopCount;
            public int DistrictFrame;
            public int ConstructionFrame;
            public int VisibilityFrame;
            public int TerritoryFrame;
            public int EmpireIndex;
            public ArmyActionType Mode;
        }

        private readonly GameSurface m_Game;
        private readonly Dictionary<int, CacheEntry> m_Cache = new Dictionary<int, CacheEntry>();
        private readonly HashSet<int> m_SeenTiles = new HashSet<int>();
        private readonly List<Pick> m_Merged = new List<Pick>();

        public PickComputation(GameSurface game)
        {
            m_Game = game;
        }

        public Pick[] Compute(CursorContext context)
        {
            var pass = new Pass { Context = context };

            if (!m_Game.GetCurrentFrames(out pass.DistrictFrame, out pass.ConstructionFrame, out pass.VisibilityFrame))
                return null;
            if (!m_Game.GetTerritoryFrame(out pass.TerritoryFrame))
                return null;

            // Mode dispatch on the stamped mode: the mode-policy table fails an unwired mode closed.
            if (!ModePolicy.IsWired(context.Mode))
                return null;

            object districtDefinition;
            if (!m_Game.TryGetDistrictDefinition(context.Mode, out districtDefinition))
                return null; // no definition for the mode: fail closed
            pass.DistrictDefinition = districtDefinition;

            // Army-level gate, once per pass, with no cost preview: any hit skips the scan.
            ArmyActionFailureFlags armyFailures;
            if (!m_Game.TryGetArmyGateFailures(context.Mode, context.Army, out armyFailures))
                return null; // the gate cannot be answered: fail closed
            if (armyFailures != ArmyActionFailureFlags.None)
                return new Pick[0];

            m_Merged.Clear();
            m_SeenTiles.Clear();
            AddUnique(GetTerritoryPicks(context.HoveredTerritory, context.TopHovered, pass));

            var picks = m_Merged.ToArray();
            Array.Sort(picks, (a, b) => a.Tile.CompareTo(b.Tile));
            return picks;
        }

        private void AddUnique(Pick[] picks)
        {
            for (int i = 0; i < picks.Length; i++)
                if (m_SeenTiles.Add(picks[i].Tile))
                    m_Merged.Add(picks[i]);
        }

        private Pick[] GetTerritoryPicks(int territoryIndex, int topCount, Pass pass)
        {
            CacheEntry cached;
            if (m_Cache.TryGetValue(territoryIndex, out cached)
                && cached.DistrictFrame == pass.DistrictFrame
                && cached.ConstructionFrame == pass.ConstructionFrame
                && cached.VisibilityFrame == pass.VisibilityFrame
                && cached.TerritoryFrame == pass.TerritoryFrame
                && cached.TopCount == topCount
                && cached.EmpireIndex == pass.Context.EmpireIndex
                && cached.Mode == pass.Context.Mode)
            {
                return cached.Picks;
            }

            var picks = Admit(Rank(Evaluated(territoryIndex, pass)), topCount);

            m_Cache[territoryIndex] = new CacheEntry
            {
                Picks = picks,
                TopCount = topCount,
                DistrictFrame = pass.DistrictFrame,
                ConstructionFrame = pass.ConstructionFrame,
                VisibilityFrame = pass.VisibilityFrame,
                TerritoryFrame = pass.TerritoryFrame,
                EmpireIndex = pass.Context.EmpireIndex,
                Mode = pass.Context.Mode
            };
            return picks;
        }

        /// <summary>Every scoreable tile of the territory: the territory filter, then the tile
        /// pre-filter, then the tile gate, then scoring. Order is SPEC section 2 steps 4 to 7.</summary>
        private List<Pick> Evaluated(int territoryIndex, Pass pass)
        {
            var survivors = new List<Pick>();

            TerritoryFacts facts;
            // Unknown is a distinct outcome: an unknown territory never advises.
            if (m_Game.GetTerritoryFacts(territoryIndex, out facts) != TerritoryFactsResult.Known)
                return survivors;
            if (!ModePolicy.IsTerritoryClaimable(pass.Context.Mode, facts))
                return survivors;

            var tiles = facts.Tiles;
            for (int i = 0; i < tiles.Length; i++)
            {
                var tile = tiles[i];
                if (m_Game.IsTileWater(tile)) continue;                       // per tile, never per territory
                if (!m_Game.IsTileExplored(tile, pass.Context.EmpireIndex)) continue; // the gate's any-bit predicate
                if (!m_Game.IsSettlementSiteTile(pass.DistrictDefinition, tile)) continue; // terrain + POI
                if (!GateIsAdmissible(tile, pass)) continue;                 // the mode's tile gate, masked
                if (m_Game.IsTileResourceTile(tile)) continue;               // the kept resource-tile exclusion
                DistrictPlacementEvaluation.ValidTile validTile;
                FixedPoint score;
                if (!m_Game.EvaluateTile(pass.Context.Army, tile, pass.DistrictDefinition, out validTile, out score)) continue;
                survivors.Add(new Pick { Tile = tile, Score = score, Data = validTile });
            }
            return survivors;
        }

        /// <summary>The mode's tile gate with the membership mask {NotEnoughMoney,
        /// NotEnoughInfluence}: an unaffordable tile stays, every other failure drops it.
        /// No affordability prefilter: this call runs on every pre-filter survivor.</summary>
        private bool GateIsAdmissible(int tileIndex, Pass pass)
        {
            ArmyActionFailureFlags failures;
            FixedPoint moneyCost, influenceCost, unitCost;
            if (!m_Game.TryGetTileGateFailures(pass.Context.Mode, pass.Context.Army, tileIndex,
                maskMoneyAndInfluence: true, out failures, out moneyCost, out influenceCost, out unitCost))
                return false; // the gate cannot be answered: fail closed
            return failures == ArmyActionFailureFlags.None;
        }

        /// <summary>Score descending, tile index ascending. No distance tie-break, no
        /// territory-border rule, no FIMS floor: the score is ranked as scored.</summary>
        private static List<Pick> Rank(List<Pick> evaluated)
        {
            evaluated.Sort((a, b) => b.Score < a.Score ? -1 : (a.Score < b.Score ? 1 : a.Tile.CompareTo(b.Tile)));
            return evaluated;
        }

        /// <summary>Admission: the clamped quota, then every tile tied with the last admitted
        /// score (the full top tie set). No tie cap, no star cap.</summary>
        private static Pick[] Admit(List<Pick> ranked, int quota)
        {
            int take = Math.Min(quota, ranked.Count);
            if (take > 0)
            {
                var lastAdmittedScore = ranked[take - 1].Score;
                while (take < ranked.Count && ranked[take].Score == lastAdmittedScore)
                    take++;
            }
            var admitted = new Pick[take];
            for (int i = 0; i < take; i++) admitted[i] = ranked[i];
            return admitted;
        }
    }
}
