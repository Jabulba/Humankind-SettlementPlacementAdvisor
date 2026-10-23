using System;
using System.Collections.Generic;
using Amplitude; // FixedPoint (namespace Amplitude, assembly Amplitude.Framework.dll).
using Amplitude.Mercury.Data.Simulation; // ArmyActionType.
using Amplitude.Mercury.Interop;
using Amplitude.Mercury.Simulation;
using SnapshotTerritory = Amplitude.Mercury.Interop.AI.Entities.Territory; // placement-neighborhood edges

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
    /// One row of the mode-policy table (SPEC section 3): the mode, the district definition it
    /// scores with, the tile gate it asks, and the army gates it asks before any tile, all named by
    /// the GameApi member labels the startup probe binds, each with its calling convention. This
    /// table is the single source: GameApi dispatches gates and definitions off it and holds no
    /// mode switch of its own.
    /// </summary>
    internal struct ModeRow
    {
        public ArmyActionType Mode;
        public string DefinitionLabel;
        public string TileGateLabel;
        public string TileGateShape;
        public string[] ArmyGateLabels;  // call order: the shared army-state rows first
        public string[] ArmyGateShapes;  // one shape per label, same indexes
    }

    /// <summary>
    /// The mode-policy table (SPEC section 3): which modes the pipeline runs, and per mode its
    /// territory filter, its district definition, its tile gate and its army gates. A mode the table
    /// has not wired fails closed: the advisor advises nothing and vanilla stays in charge.
    /// </summary>
    internal static class ModePolicy
    {
        /// <summary>The game member names this table's rows name. They live here, in the mode-policy
        /// table, and GameApi's startup-bound member table binds exactly these names, so a row can
        /// only name a member the startup probe checked (AGENTS: an unresolved name is a load
        /// failure).</summary>
        public const string CampDefinitionLabel = "DepartmentOfTheInterior.CampDefinition";
        public const string CityCenterDefinitionLabel = "DepartmentOfTheInterior.CityCenterDefinition";
        public const string CommonArmyGateLabel = "ArmyActionHelper.FillCommonArmyFailures(army)";
        public const string CampTileGateLabel = "ArmyActionHelper.FillCreateCampFailures(tile)";
        public const string CampArmyGateLabel = "ArmyActionHelper.FillCreateCampFailures(army)";
        public const string FoundCityTileGateLabel = "ArmyActionHelper.FillFoundCityFailures(position)";
        public const string FoundCityArmyGateLabel = "ArmyActionHelper.FillFoundCityFailures(army)";
        public const string SettlerTileGateLabel = "ArmyActionHelper.FillSettlerAbilityFailures(tile)";
        public const string SettlerArmyGateLabel = "ArmyActionHelper.FillSettlerAbilityFailures(army)";

        /// <summary>
        /// The wired rows: settlement placement, city founding, and settler city founding. Each
        /// army-gate list starts with the shared army-state rows (ArmyActionHelper.cs:365-378:
        /// decoy ActionProhibited, ArmyLockedByBattle, ArmyLocked) because the camp and settler
        /// army overloads do not run them and the city row runs them twice harmlessly (flags
        /// accumulate). Anything unmapped has no row.
        /// </summary>
        private static readonly ModeRow[] s_Rows = new ModeRow[]
        {
            new ModeRow
            {
                Mode = ArmyActionType.CreateCamp,
                DefinitionLabel = CampDefinitionLabel,
                TileGateLabel = CampTileGateLabel,
                TileGateShape = "campTile",
                ArmyGateLabels = new string[] { CommonArmyGateLabel, CampArmyGateLabel },
                ArmyGateShapes = new string[] { "armyFlags", "campArmy" },
            },
            new ModeRow
            {
                Mode = ArmyActionType.FoundCity,
                DefinitionLabel = CityCenterDefinitionLabel,
                TileGateLabel = FoundCityTileGateLabel,
                TileGateShape = "positionFlags",
                ArmyGateLabels = new string[] { CommonArmyGateLabel, FoundCityArmyGateLabel },
                ArmyGateShapes = new string[] { "armyFlags", "armyFlags" },
            },
            new ModeRow
            {
                Mode = ArmyActionType.FoundCityWithSettler,
                DefinitionLabel = CityCenterDefinitionLabel,
                TileGateLabel = SettlerTileGateLabel,
                TileGateShape = "tileFlags",
                ArmyGateLabels = new string[] { CommonArmyGateLabel, SettlerArmyGateLabel },
                ArmyGateShapes = new string[] { "armyFlags", "armyFlags" },
            },
        };

        /// <summary>A mode the table has a row for is a mode the pipeline runs for.</summary>
        public static bool IsWired(ArmyActionType mode)
        {
            ModeRow row;
            return TryGetRow(mode, out row);
        }

        /// <summary>The mode's row, the one lookup every gate, definition and filter dispatch
        /// goes through. False for a mode the table has not wired.</summary>
        public static bool TryGetRow(ArmyActionType mode, out ModeRow row)
        {
            for (int i = 0; i < s_Rows.Length; i++)
            {
                if (s_Rows[i].Mode != mode) continue;
                row = s_Rows[i];
                return true;
            }
            row = default(ModeRow);
            return false;
        }

        /// <summary>
        /// Claimable territory for the mode. Camp row: drop IsEdgeOfTheWorld
        /// (ArmyActionHelper.cs:510-514) and drop (Claimed || SettlementGUID != 0) (515-518).
        /// FoundCity row: drop IsEdgeOfTheWorld (645-648) and drop SettlementGUID != 0 (649-652) -
        /// the city gate puts no Claimed term on it, so a territory claimed with no settlement on it
        /// is claimable for a city. Settler row (740-753, replaceability 763-771): drop
        /// IsEdgeOfTheWorld and drop SettlementGUID != 0 EXCEPT the empire's own camp/pre-camp
        /// settlement, which the settler replaces - the gate clears it (no TerritoryAlreadyClaimed,
        /// no CannotSettleOnTerritoryWithCity) while the district-occupancy rule treats a camp or
        /// pre-camp as replaceable. An own city, or any other empire's settlement, stays out. A
        /// territory claimed with no settlement on it stays claimable (no Claimed term). An unwired
        /// mode claims nothing.
        /// Never a filter: the territory ocean flag, AnyClaimableTile, DemilitarizedByEmpireIndex,
        /// close-border bits.
        /// </summary>
        public static bool IsTerritoryClaimable(ArmyActionType mode, TerritoryFacts facts, int empireIndex)
        {
            if (facts.IsEdgeOfTheWorld) return false;
            if (mode == ArmyActionType.CreateCamp) return !facts.Claimed && !facts.HasSettlement;
            if (mode == ArmyActionType.FoundCity) return !facts.HasSettlement;
            if (mode == ArmyActionType.FoundCityWithSettler)
                return !facts.HasSettlement
                    || (facts.OwnerEmpireIndex == empireIndex && !facts.IsOwnedByCity);
            return false; // no row in the table: fail closed
        }
    }

    /// <summary>
    /// Which tiles to advise for a cursor context. Pure: reads the game only through
    /// GameSurface and returns ascending, unique picks (null = game facts unavailable).
    ///
    /// Pipeline per scan pass (SPEC section 2): mode dispatch, the army-level gate once per pass,
    /// then per territory of the placement neighborhood the mode's territory filter, the tile
    /// pre-filter (water, explored with the gate's any-bit predicate, terrain + POI with the
    /// mode's district definition), the mode's tile gate with the money/influence mask, scoring on
    /// the vanilla weight table with the resource-tile exclusion kept, and admission as the clamped
    /// quota plus the full top tie set.
    ///
    /// The placement neighborhood is the hovered territory (level 1) plus RangeLevels - 1 expansions
    /// beyond it, each stepping over every adjacency edge the placement-neighborhood relation
    /// reports: sea, cliff, mountain and closed-border edges each count as a hop, with no transition
    /// filter, no reachability, no movement points, no zone of control, no cap, no budget and no
    /// truncation. Unknown adjacency degrades the pass to the hovered territory's admission set
    /// alone and logs the surface failure. The per-territory cache is keyed by territory, top count,
    /// the frames read here (the world claim frame included), and the territory's own claim stamp:
    /// a claim-frame bump recomputes only the territory whose claim facts moved, and a mouse move
    /// costs cache hits plus the territories newly entering the neighborhood. Callers never thread
    /// frames. Rules: README.md.
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

        /// <summary>
        /// One territory's cached admission set with everything that can invalidate it: the
        /// global frames, the quota, the empire, the placement mode, the world claim frame, and
        /// the territory's own claim stamp (SPEC section 2, cadence and cache). The stamp keeps a
        /// claim, unclaim, owner steal, exploration or patrol change scoped to the territory it
        /// changed: a claim-frame bump re-runs the tile work of a territory only when that
        /// territory's own claim facts moved.
        /// </summary>
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

            // The per-territory claim stamp: this territory's claim-frame-relevant facts.
            public bool Known;
            public bool IsEdgeOfTheWorld;
            public bool Claimed;
            public bool HasSettlement;
            public bool IsOwnedByCity;
            public int OwnerEmpireIndex;
            public int TileCount;
        }

        private readonly GameSurface m_Game;
        private readonly Dictionary<int, CacheEntry> m_Cache = new Dictionary<int, CacheEntry>();
        private readonly HashSet<int> m_SeenTiles = new HashSet<int>();
        private readonly List<Pick> m_Merged = new List<Pick>();
        private readonly HashSet<int> m_Visited = new HashSet<int>();
        private List<int> m_Frontier = new List<int>(); // the territories of the current level
        private List<int> m_NextFrontier = new List<int>(); // swap buffer: no per-level allocation

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

            var hoveredPicks = GetTerritoryPicks(context.HoveredTerritory, context.TopHovered, pass);
            m_Merged.Clear();
            m_SeenTiles.Clear();
            AddUnique(hoveredPicks);
            if (AddNeighbourhood(pass))
            {
                // SPEC section 2 step 3: an unknown adjacency degrades the pass to hovered-only,
                // so every territory reached at level 2 or deeper drops out of the published set.
                m_Merged.Clear();
                m_SeenTiles.Clear();
                AddUnique(hoveredPicks);
            }

            var picks = m_Merged.ToArray();
            Array.Sort(picks, (a, b) => a.Tile.CompareTo(b.Tile));
            return picks;
        }

        /// <summary>
        /// The placement neighborhood beyond the hovered territory: RangeLevels hops over every
        /// adjacency edge (level 1 is the hovered territory, so RangeLevels - 1 expansions run).
        /// Every edge counts as a hop - sea, cliff, mountain and closed-border included - and
        /// every territory reached contributes its own admission set at the neighbour quota,
        /// served by the per-territory cache: a mouse move costs cache hits plus the territories
        /// newly entering the neighborhood. Any territory whose edges the surface cannot answer
        /// degrades the whole pass to the hovered territory's admission set alone (SPEC section 2
        /// step 3), logs the surface failure, and returns true.
        /// </summary>
        private bool AddNeighbourhood(Pass pass)
        {
            m_Visited.Clear();
            m_Visited.Add(pass.Context.HoveredTerritory);
            m_Frontier.Clear();
            m_Frontier.Add(pass.Context.HoveredTerritory);

            for (int level = 2; level <= pass.Context.RangeLevels && m_Frontier.Count > 0; level++)
            {
                m_NextFrontier.Clear();
                for (int i = 0; i < m_Frontier.Count; i++)
                {
                    SnapshotTerritory.Adjacency[] edges;
                    if (!m_Game.TryGetAdjacentTerritories(m_Frontier[i], out edges))
                    {
                        GameApi.Fail("placement-neighborhood adjacency: territory " + m_Frontier[i]);
                        return true; // unknown edges: the whole pass degrades to hovered-only
                    }
                    for (int e = 0; e < edges.Length; e++)
                    {
                        int territory = edges[e].TerritoryIndex;
                        if (!m_Visited.Add(territory)) continue; // one admission set per territory
                        AddUnique(GetTerritoryPicks(territory, pass.Context.TopNeighbour, pass));
                        m_NextFrontier.Add(territory);
                    }
                }
                var walked = m_Frontier;
                m_Frontier = m_NextFrontier;
                m_NextFrontier = walked;
            }

            return false; // every frontier answered its edges: the full neighborhood stands
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
            bool keyed = m_Cache.TryGetValue(territoryIndex, out cached)
                && cached.DistrictFrame == pass.DistrictFrame
                && cached.ConstructionFrame == pass.ConstructionFrame
                && cached.VisibilityFrame == pass.VisibilityFrame
                && cached.TopCount == topCount
                && cached.EmpireIndex == pass.Context.EmpireIndex
                && cached.Mode == pass.Context.Mode;
            if (!keyed)
                return Recompute(territoryIndex, topCount, pass);

            if (cached.TerritoryFrame == pass.TerritoryFrame)
                return cached.Picks; // the claim frame is unmoved: no claim fact can have changed

            // The claim frame moved. Decide from this territory's own facts: an unchanged stamp
            // means the bump came from elsewhere in the world, so the cached admission set stands
            // and the tile gate is asked no tile of this territory.
            TerritoryFacts facts;
            if (m_Game.GetTerritoryFacts(territoryIndex, out facts) == TerritoryFactsResult.Known
                && StampMatches(cached, facts))
            {
                cached.TerritoryFrame = pass.TerritoryFrame;
                m_Cache[territoryIndex] = cached;
                return cached.Picks;
            }
            return Recompute(territoryIndex, topCount, pass);
        }

        /// <summary>The cached territory's claim stamp against its facts now.</summary>
        private static bool StampMatches(CacheEntry cached, TerritoryFacts facts)
        {
            return cached.Known
                && cached.IsEdgeOfTheWorld == facts.IsEdgeOfTheWorld
                && cached.Claimed == facts.Claimed
                && cached.HasSettlement == facts.HasSettlement
                && cached.IsOwnedByCity == facts.IsOwnedByCity
                && cached.OwnerEmpireIndex == facts.OwnerEmpireIndex
                && cached.TileCount == facts.Tiles.Length;
        }

        /// <summary>The territory's full scan pass, cached with its claim stamp: the mode's
        /// territory filter, then the tile pre-filter, then the tile gate, then scoring. Order is
        /// SPEC section 2 steps 4 to 7.</summary>
        private Pick[] Recompute(int territoryIndex, int topCount, Pass pass)
        {
            var survivors = new List<Pick>();

            TerritoryFacts facts;
            // Unknown is a distinct outcome: an unknown territory never advises, and its stamp
            // records the unknown read so a later known read recomputes it.
            bool known = m_Game.GetTerritoryFacts(territoryIndex, out facts) == TerritoryFactsResult.Known;
            if (known && ModePolicy.IsTerritoryClaimable(pass.Context.Mode, facts, pass.Context.EmpireIndex))
            {
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
            }

            var picks = Admit(Rank(survivors), topCount);
            m_Cache[territoryIndex] = new CacheEntry
            {
                Picks = picks,
                TopCount = topCount,
                DistrictFrame = pass.DistrictFrame,
                ConstructionFrame = pass.ConstructionFrame,
                VisibilityFrame = pass.VisibilityFrame,
                TerritoryFrame = pass.TerritoryFrame,
                EmpireIndex = pass.Context.EmpireIndex,
                Mode = pass.Context.Mode,
                Known = known,
                IsEdgeOfTheWorld = facts.IsEdgeOfTheWorld,
                Claimed = facts.Claimed,
                HasSettlement = facts.HasSettlement,
                IsOwnedByCity = facts.IsOwnedByCity,
                OwnerEmpireIndex = facts.OwnerEmpireIndex,
                TileCount = known ? facts.Tiles.Length : 0
            };
            return picks;
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
