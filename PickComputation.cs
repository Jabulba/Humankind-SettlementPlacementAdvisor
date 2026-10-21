using System;
using System.Collections.Generic;
using Amplitude; // FixedPoint (namespace Amplitude, assembly Amplitude.Framework.dll).
using Amplitude.Mercury.Interop;
using Amplitude.Mercury.Simulation;

namespace CampPlacementAdvisor
{
    /// <summary>One advisor pick: the tile, its weighted score, and vanilla's evaluation record for it.</summary>
    internal struct Pick
    {
        public int Tile;
        public FixedPoint Score;
        public DistrictPlacementEvaluation.ValidTile Data;
    }

    /// <summary>
    /// THE CURSOR CONTEXT: the pick computation's full input for one refresh: hovered
    /// territory, placing army + empire, and the advised counts. Count clamping lives here
    /// (counts are mandatory, minimum 1), not in the caller.
    /// </summary>
    internal struct CursorContext
    {
        public int HoveredTerritory;
        public object Army;
        public int EmpireIndex;
        public int TopHovered;
        public int TopNeighbour;
        public int RangeLevels;

        public static CursorContext For(int hoveredTerritory, object army, int empireIndex,
            int topHovered, int topNeighbour, int rangeLevels)
        {
            return new CursorContext
            {
                HoveredTerritory = hoveredTerritory,
                Army = army,
                EmpireIndex = empireIndex,
                TopHovered = Math.Max(1, topHovered),
                TopNeighbour = Math.Max(1, topNeighbour),
                RangeLevels = Math.Max(1, rangeLevels),
            };
        }
    }

    /// <summary>
    /// THE PICK COMPUTATION: which tiles to advise for a cursor context. Pure: it evaluates
    /// and reads the territory graph through the Game Surface, and touches no evaluation
    /// array, no published pick set, no presentation. Null = game facts unavailable. Picks
    /// come back in canonical order (ascending by tile, unique). The per-territory cache is
    /// keyed by territory, top count, empire, and the rule frames READ HERE (callers never
    /// thread them); budgeted per refresh. Rules: README.md (mandatory counts, no stars on
    /// unexplored/owned/ocean tiles).
    /// </summary>
    internal sealed class PickComputation
    {
        /// <summary>Hard cap on tile evaluations per refresh (cache-miss territories only).</summary>
        public const int MaxEvaluationsPerRefresh = 1200;

        /// <summary>Cap on collected in-range territories: bounds the evaluation tail (EvaluationWriter).</summary>
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
                return null; // game facts unavailable: the advisor deactivates

            m_Budget = MaxEvaluationsPerRefresh;
            m_BudgetExceededLogged = false;

            m_Merged.Clear();
            m_SeenTiles.Clear();
            AddUnique(GetTerritoryPicks(context.HoveredTerritory, context.TopHovered, context.Army,
                districtFrame, constructionFrame, visibilityFrame, context.EmpireIndex));

            CollectRangeTerritories(context.HoveredTerritory, context.RangeLevels);
            for (int t = 0; t < m_RangeTerritories.Count; t++)
            {
                AddUnique(GetTerritoryPicks(m_RangeTerritories[t], context.TopNeighbour, context.Army,
                    districtFrame, constructionFrame, visibilityFrame, context.EmpireIndex));
            }

            var picks = m_Merged.ToArray();
            Array.Sort(picks, (a, b) => a.Tile.CompareTo(b.Tile)); // canonical ascending order
            return picks;
        }

        private void AddUnique(Pick[] picks)
        {
            for (int i = 0; i < picks.Length; i++)
                if (m_SeenTiles.Add(picks[i].Tile))
                    m_Merged.Add(picks[i]);
        }

        /// <summary>
        /// Territories in range excluding the center: center counts as level 1, so
        /// rangeLevels = 3 adds all adjacent, then all adjacent-to-adjacent.
        /// Capped at MaxRangeTerritories so the tail stays bounded.
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
            var adjacent = m_Game.AdjacentTerritories(territoryIndex);
            for (int i = 0; i < adjacent.Length; i++)
            {
                if (m_SeenTerritories.Add(adjacent[i]))
                    output.Add(adjacent[i]);
            }
        }

        private Pick[] GetTerritoryPicks(
            int territoryIndex, int topCount, object army,
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
            bool isOcean, isClaimed;
            int[] tiles;
            if (m_Game.GetTerritoryFacts(territoryIndex, out isOcean, out isClaimed, out tiles)
                && !isOcean && !isClaimed)
            {
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
                    if (!m_Game.IsCampableTile(tile)) continue;
                    if (!m_Game.IsTileExplored(tile, empireIndex)) continue;
                    DistrictPlacementEvaluation.ValidTile validTile;
                    FixedPoint score;
                    if (!m_Game.EvaluateTile(army, tile, out validTile, out score)) continue;
                    evaluated.Add(new Pick { Tile = tile, Score = score, Data = validTile });
                }

                evaluated.Sort((a, b) => b.Score < a.Score ? -1 : (a.Score < b.Score ? 1 : a.Tile.CompareTo(b.Tile)));
                var take = Math.Min(topCount, evaluated.Count);
                for (int i = 0; i < take; i++) picks.Add(evaluated[i]);
            }

            // Truncated (budget-starved) results are NOT cached: frames are static during
            // a camp session, so a cached partial result would freeze missing stars until
            // the next frame change. Re-evaluate such territories on the next refresh.
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
