using System;
using System.Collections.Generic;
using Amplitude; // FixedPoint (namespace Amplitude, assembly Amplitude.Framework.dll).
using Amplitude.Mercury.Data.Simulation; // ArmyActionType.
using Amplitude.Mercury.Interop;
using Amplitude.Mercury.Interop.AI.Entities; // Territory.Adjacency (the surface's adjacency edges)
using Amplitude.Mercury.Simulation;
using TerritoryTransition = Amplitude.Mercury.Data.AI.TerritoryTransition; // edge transition flags

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
    /// Which tiles to advise for a cursor context. Pure: reads the game only through
    /// GameSurface and returns ascending, unique picks (null = game facts unavailable). The
    /// per-territory cache is keyed by territory, top count, empire, and the frames read here;
    /// callers never thread frames. Evaluation is budgeted per refresh. Rules: README.md.
    /// </summary>
    internal sealed class PickComputation
    {
        /// <summary>Hard cap on tile evaluations per refresh (cache-miss territories only).</summary>
        public const int MaxEvaluationsPerRefresh = 1200;

        /// <summary>Bounds the in-range territories, and therefore the evaluation tail size.</summary>
        public const int MaxRangeTerritories = 64;

        private struct CacheEntry
        {
            public Pick[] Picks;
            public int TopCount;
            public int DistrictFrame;
            public int ConstructionFrame;
            public int VisibilityFrame;
            public int EmpireIndex;
        }

        private readonly GameSurface m_Game;
        private readonly Dictionary<int, CacheEntry> m_Cache = new Dictionary<int, CacheEntry>();
        private readonly HashSet<int> m_SeenTerritories = new HashSet<int>();
        private readonly HashSet<int> m_SeenTiles = new HashSet<int>();
        private readonly List<Pick> m_Merged = new List<Pick>();
        private readonly List<int> m_RangeTerritories = new List<int>();
        private List<int> m_Frontier = new List<int>();
        private List<int> m_NextFrontier = new List<int>();
        private int m_Budget;
        private bool m_BudgetExceededLogged;

        public PickComputation(GameSurface game)
        {
            m_Game = game;
        }

        public Pick[] Compute(CursorContext context)
        {
            int districtFrame, constructionFrame, visibilityFrame;
            if (!m_Game.GetCurrentFrames(out districtFrame, out constructionFrame, out visibilityFrame))
                return null;

            object districtDefinition;
            if (!m_Game.TryGetDistrictDefinition(context.Mode, out districtDefinition))
                return null; // unmapped mode: fail closed

            m_Budget = MaxEvaluationsPerRefresh;
            m_BudgetExceededLogged = false;

            m_Merged.Clear();
            m_SeenTiles.Clear();
            AddUnique(GetTerritoryPicks(context.HoveredTerritory, context.TopHovered, context.Army, districtDefinition,
                districtFrame, constructionFrame, visibilityFrame, context.EmpireIndex));

            CollectRangeTerritories(context.HoveredTerritory, context.RangeLevels);
            for (int t = 0; t < m_RangeTerritories.Count; t++)
            {
                AddUnique(GetTerritoryPicks(m_RangeTerritories[t], context.TopNeighbour, context.Army, districtDefinition,
                    districtFrame, constructionFrame, visibilityFrame, context.EmpireIndex));
            }

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

        /// <summary>
        /// Territories in range excluding the center. Center counts as level 1, so a territory
        /// at hop d is in range when d + 1 &lt;= rangeLevels. Only the edges a ground army
        /// crosses (TerritoryTransition.Open) count as a hop: the surface reports every
        /// adjacency edge, the sea, cliff, mountain and closed edges are dropped here.
        /// Capped so the evaluation tail stays bounded.
        /// </summary>
        private void CollectRangeTerritories(int hoveredTerritory, int rangeLevels)
        {
            m_RangeTerritories.Clear();
            m_SeenTerritories.Clear();
            m_SeenTerritories.Add(hoveredTerritory);
            m_Frontier.Clear();
            m_NextFrontier.Clear();

            for (int level = 1; level < rangeLevels; level++)
            {
                if (m_RangeTerritories.Count >= MaxRangeTerritories)
                {
                    GameApi.Fail("range territories capped at " + MaxRangeTerritories + "; further territories ignored");
                    return;
                }

                m_NextFrontier.Clear();
                if (level == 1)
                {
                    AddAdjacent(hoveredTerritory, m_NextFrontier);
                }
                else
                {
                    for (int i = 0; i < m_Frontier.Count; i++)
                        AddAdjacent(m_Frontier[i], m_NextFrontier);
                }

                for (int i = 0; i < m_NextFrontier.Count && m_RangeTerritories.Count < MaxRangeTerritories; i++)
                    m_RangeTerritories.Add(m_NextFrontier[i]);

                var swap = m_Frontier;
                m_Frontier = m_NextFrontier;
                m_NextFrontier = swap;
                if (m_Frontier.Count == 0)
                    break;
            }
        }

        private void AddAdjacent(int territoryIndex, List<int> output)
        {
            var edges = m_Game.AdjacentTerritories(territoryIndex);
            for (int i = 0; i < edges.Length; i++)
            {
                if ((edges[i].Transition & TerritoryTransition.Open) == TerritoryTransition.None) continue;
                if (m_SeenTerritories.Add(edges[i].TerritoryIndex))
                    output.Add(edges[i].TerritoryIndex);
            }
        }

        private Pick[] GetTerritoryPicks(
            int territoryIndex, int topCount, object army, object districtDefinition,
            int districtFrame, int constructionFrame, int visibilityFrame, int empireIndex)
        {
            CacheEntry cached;
            if (m_Cache.TryGetValue(territoryIndex, out cached)
                && cached.DistrictFrame == districtFrame
                && cached.ConstructionFrame == constructionFrame
                && cached.VisibilityFrame == visibilityFrame
                && cached.TopCount == topCount
                && cached.EmpireIndex == empireIndex)
            {
                return cached.Picks;
            }

            var picks = new List<Pick>(topCount);
            var truncated = false;
            TerritoryFacts facts;
            if (m_Game.GetTerritoryFacts(territoryIndex, out facts) == TerritoryFactsResult.Known
                && !facts.Claimed) // Unknown is a distinct outcome: an unknown territory never advises.
            {
                var tiles = facts.Tiles;
                var evaluated = new List<Pick>(tiles.Length);
                for (int i = 0; i < tiles.Length; i++)
                {
                    if (m_Budget <= 0)
                    {
                        if (!m_BudgetExceededLogged)
                        {
                            m_BudgetExceededLogged = true;
                            GameApi.Fail("evaluation budget (" + MaxEvaluationsPerRefresh + ") reached for this refresh; later territories truncated");
                        }
                        truncated = true;
                        break;
                    }
                    m_Budget--;

                    var tile = tiles[i];
                    if (!m_Game.IsSettlementSiteTile(districtDefinition, tile) || m_Game.IsTileWater(tile)) continue;
                    if (!m_Game.IsTileExplored(tile, empireIndex)) continue;
                    DistrictPlacementEvaluation.ValidTile validTile;
                    FixedPoint score;
                    if (!m_Game.EvaluateTile(army, tile, districtDefinition, out validTile, out score)) continue;
                    evaluated.Add(new Pick { Tile = tile, Score = score, Data = validTile });
                }

                evaluated.Sort((a, b) => b.Score < a.Score ? -1 : (a.Score < b.Score ? 1 : a.Tile.CompareTo(b.Tile)));
                var take = Math.Min(topCount, evaluated.Count);
                for (int i = 0; i < take; i++) picks.Add(evaluated[i]);
            }

            // Truncated (budget-starved) results are not cached: frames are static during a
            // settlement session, so a cached partial result would hide picks indefinitely.
            if (!truncated)
            {
                m_Cache[territoryIndex] = new CacheEntry
                {
                    Picks = picks.ToArray(),
                    TopCount = topCount,
                    DistrictFrame = districtFrame,
                    ConstructionFrame = constructionFrame,
                    VisibilityFrame = visibilityFrame,
                    EmpireIndex = empireIndex
                };
            }
            return picks.ToArray();
        }
    }
}
