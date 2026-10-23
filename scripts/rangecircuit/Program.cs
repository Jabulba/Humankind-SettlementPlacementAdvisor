using System;
using System.Collections.Generic;
using Amplitude;
using Amplitude.Mercury.Data.Simulation; // ArmyActionType
using Amplitude.Mercury.Interop;
using Amplitude.Mercury.Interop.AI.Entities; // Territory.Adjacency
using Amplitude.Mercury.Simulation;
using SettlementPlacementAdvisor;
using TerritoryTransition = Amplitude.Mercury.Data.AI.TerritoryTransition;

/// <summary>
/// DEBUG [rngc2] synthetic graph surface. Territory k has three placeable/explored tiles
/// (100+10k .. 102+10k) scored by tile index, so its best tile is the last one. Adjacency uses
/// real Territory.Adjacency edges through the surface member, so the production land-hop filter
/// and the range BFS are exercised together.
/// </summary>
internal sealed class GraphSurface : GameSurface
{
    internal struct Edge
    {
        public int To;
        public TerritoryTransition Transition;
    }

    public int TerritoryCount;
    public bool[] IsOcean;
    public bool[] UnknownFacts; // territories whose snapshot facts are missing
    public List<Edge>[] Edges;
    public string DefinitionRequests = ""; // every district-definition request the rules made

    public bool Ready { get { return true; } }
    public object TryGetArmy(SimulationEntityGUID guid) { return "army"; }
    public int GetArmyEmpireIndex(object army) { return 0; }

    public bool TryGetDistrictDefinition(ArmyActionType mode, out object districtDefinition)
    {
        DefinitionRequests += mode + "=";
        if (mode == ArmyActionType.CreateCamp) { districtDefinition = "camp"; DefinitionRequests += "camp\n"; return true; }
        if (mode == ArmyActionType.FoundCity || mode == ArmyActionType.FoundCityWithSettler) { districtDefinition = "cityCenter"; DefinitionRequests += "cityCenter\n"; return true; }
        districtDefinition = null;
        DefinitionRequests += "none\n";
        return false;
    }

    public bool IsSettlementSiteTile(object districtDefinition, int tileIndex) { return districtDefinition != null; }
    public bool IsTileWater(int tileIndex) { return false; }
    public bool IsTileExplored(int tileIndex, int empireIndex) { return true; }

    public bool EvaluateTile(object army, int tileIndex, object districtDefinition, out DistrictPlacementEvaluation.ValidTile validTile, out FixedPoint score)
    {
        var vt = default(DistrictPlacementEvaluation.ValidTile);
        vt.TileIndex = tileIndex;
        validTile = vt;
        score = tileIndex;
        return true;
    }

    public bool GetCurrentFrames(out int districtFrame, out int constructionFrame, out int visibilityFrame)
    {
        districtFrame = 1; constructionFrame = 1; visibilityFrame = 1;
        return true;
    }

    public bool GetTerritoryFrame(out int territoryFrame)
    {
        territoryFrame = 1;
        return true;
    }

    public int TerritoryOfTile(int tileIndex)
    {
        int t = (tileIndex - 100) / 10;
        return (t < 0 || t >= TerritoryCount) ? -1 : t;
    }

    public TerritoryFactsResult GetTerritoryFacts(int territoryIndex, out TerritoryFacts facts)
    {
        facts = default(TerritoryFacts);
        if (territoryIndex < 0 || territoryIndex >= TerritoryCount || UnknownFacts[territoryIndex])
            return TerritoryFactsResult.Unknown;
        facts.IsEdgeOfTheWorld = false;
        facts.Claimed = false;
        facts.HasSettlement = false;
        facts.OwnerEmpireIndex = -1;
        facts.Tiles = new int[] { 100 + territoryIndex * 10, 101 + territoryIndex * 10, 102 + territoryIndex * 10 };
        return TerritoryFactsResult.Known;
    }

    public Territory.Adjacency[] AdjacentTerritories(int territoryIndex)
    {
        var edges = Edges[territoryIndex];
        var adjacencies = new Territory.Adjacency[edges.Count];
        for (int i = 0; i < edges.Count; i++)
        {
            adjacencies[i] = new Territory.Adjacency
            {
                TerritoryIndex = edges[i].To,
                Transition = edges[i].Transition
            };
        }
        return adjacencies; // every edge: the surface applies no transition filter
    }

    public bool TryGetTileGateFailures(ArmyActionType mode, object army, int tileIndex, bool maskMoneyAndInfluence, out ArmyActionFailureFlags failures, out FixedPoint moneyCost, out FixedPoint influenceCost, out FixedPoint unitCost)
    {
        failures = ArmyActionFailureFlags.None;
        moneyCost = FixedPoint.Zero;
        influenceCost = FixedPoint.Zero;
        unitCost = FixedPoint.Zero;
        return army != null;
    }

    public bool TryGetArmyGateFailures(ArmyActionType mode, object army, out ArmyActionFailureFlags failures)
    {
        failures = ArmyActionFailureFlags.None;
        return army != null;
    }

    /// <summary>
    /// Reference range: BFS over the edges a ground army crosses (Open) with the advisor's level
    /// counting (center = level 1, so a territory at hop d is in range iff d + 1 &lt;=
    /// rangeLevels). Sea, cliff and closed edges are the ones the rules must drop.
    /// </summary>
    public int[] LandRange(int hovered, int rangeLevels)
    {
        var level = new int[TerritoryCount];
        for (int i = 0; i < TerritoryCount; i++) level[i] = int.MaxValue;
        level[hovered] = 1;
        var frontier = new List<int> { hovered };
        while (frontier.Count > 0)
        {
            var next = new List<int>();
            foreach (var t in frontier)
            {
                if (level[t] >= rangeLevels) continue;
                foreach (var e in Edges[t])
                {
                    if ((e.Transition & TerritoryTransition.Open) == TerritoryTransition.None) continue;
                    if (level[e.To] <= level[t] + 1) continue;
                    level[e.To] = level[t] + 1;
                    next.Add(e.To);
                }
            }
            frontier = next;
        }
        var result = new List<int>();
        for (int i = 0; i < TerritoryCount; i++)
            if (level[i] <= rangeLevels && i != hovered && !IsOcean[i] && !UnknownFacts[i]) result.Add(i);
        result.Sort();
        return result.ToArray();
    }

    public int[] AllAdjacentIndexes(int territoryIndex)
    {
        var indexes = new List<int>();
        foreach (var e in Edges[territoryIndex]) indexes.Add(e.To);
        indexes.Sort();
        return indexes.ToArray();
    }
}

internal static class Program
{
    private static int Main()
    {
        int failures = 0;

        // A. Pure land chain: the level counting itself (center = level 1).
        var chain = Grid(7);
        for (int t = 0; t + 1 < 7; t++) Link(chain, t, t + 1, TerritoryTransition.Open);
        failures += Run("chain", chain, 0, new int[] { 1, 2, 3, 4 });

        // B. MINIMAL repro: hovered land -- sea -- land. Land range 3 must pick nothing
        // beyond the hovered territory: crossing a sea territory is not a land hop.
        var minimal = Grid(3);
        Link(minimal, 0, 1, TerritoryTransition.Coast);
        Link(minimal, 1, 2, TerritoryTransition.Coast);
        minimal.IsOcean[1] = true;
        failures += Run("minimal(land-sea-land)", minimal, 0, new int[] { 3 });

        // C. Coast chain: the sea hop also opens the coastline (T2 --Open-- T3).
        var coast = Grid(4);
        Link(coast, 0, 1, TerritoryTransition.Coast);
        Link(coast, 1, 2, TerritoryTransition.Coast);
        Link(coast, 2, 3, TerritoryTransition.Open);
        coast.IsOcean[1] = true;
        failures += Run("coast", coast, 0, new int[] { 2, 3, 4 });

        // D. Bay: one sea territory touching five land territories. Water hops turn a
        // single sea edge into a fan of "adjacent-to-adjacent" land territories.
        var bay = Grid(7);
        Link(bay, 0, 1, TerritoryTransition.Coast);
        for (int t = 2; t <= 6; t++) Link(bay, 1, t, TerritoryTransition.Coast);
        bay.IsOcean[1] = true;
        failures += Run("bay", bay, 0, new int[] { 2, 3 });

        // E. Passability edges that are not land hops: cliffs and closed borders.
        var ridge = Grid(3);
        Link(ridge, 0, 1, TerritoryTransition.Open);
        Link(ridge, 1, 2, TerritoryTransition.Cliffs);
        failures += Run("ridge(cliff edge)", ridge, 0, new int[] { 2, 3 });

        // F. The surface reports EVERY adjacency edge (no transition filter); the land-hop
        // filter is the rule's own, so the scan above stays land-only on the same graph.
        var fan = Grid(5);
        Link(fan, 0, 1, TerritoryTransition.Open);
        Link(fan, 0, 2, TerritoryTransition.Coast);
        Link(fan, 0, 3, TerritoryTransition.Cliffs);
        Link(fan, 0, 4, TerritoryTransition.Closed);
        failures += Run("fan(1 open + 3 non-land hops)", fan, 0, new int[] { 2 });
        var reported = IndexesOf(surface: fan, territory: 0);
        var expectedAll = fan.AllAdjacentIndexes(0);
        bool allEdges = reported.Length == expectedAll.Length;
        for (int i = 0; i < reported.Length && allEdges; i++)
            if (reported[i] != expectedAll[i]) allEdges = false;
        Console.WriteLine("[rngc2] adjacency surface reports=" + string.Join(",", reported)
            + " all edges=" + string.Join(",", expectedAll) + (allEdges ? "  OK" : "  MISMATCH"));
        if (!allEdges) failures++;

        // G. Unknown territory facts: a distinct outcome, never advised, while its neighbours
        // keep their picks.
        var fogged = Grid(4);
        for (int t = 0; t + 1 < 4; t++) Link(fogged, t, t + 1, TerritoryTransition.Open);
        fogged.UnknownFacts[1] = true;
        failures += Run("unknown(T1 facts missing)", fogged, 0, new int[] { 2, 3 });

        // H. Unmapped placement mode: the surface has no district definition for it, so the
        // scan fails closed and advises nothing.
        var unmapped = new PickComputation(chain).Compute(CursorContext.For(0, "army", 0, 2, 1, 2, ArmyActionType.Ransack));
        Console.WriteLine("[rngc2] unmapped mode (Ransack) picks=" + (unmapped == null ? "none" : unmapped.Length.ToString())
            + (unmapped == null ? "  OK" : "  MISMATCH"));
        if (unmapped != null) failures++;
        Console.WriteLine("[rngc2] verdict: " + (failures == 0 ? "GREEN" : "RED (" + failures + " range violations)"));
        return failures == 0 ? 0 : 1;
    }

    private static int Run(string name, GraphSurface surface, int hovered, int[] levels)
    {
        var computation = new PickComputation(surface);
        int failures = 0;

        foreach (int rangeLevels in levels)
        {
            var picks = computation.Compute(CursorContext.For(hovered, "army", 0, 2, 1, rangeLevels, ArmyActionType.CreateCamp));

            var actual = new List<string>();
            foreach (var p in picks) actual.Add("T" + surface.TerritoryOfTile(p.Tile) + "/" + p.Tile);

            var expected = new List<string> { "T" + hovered + "/" + (101 + hovered * 10), "T" + hovered + "/" + (102 + hovered * 10) };
            foreach (var t in surface.LandRange(hovered, rangeLevels)) expected.Add("T" + t + "/" + (102 + t * 10));
            expected.Sort((a, b) => Tile(a).CompareTo(Tile(b)));

            bool ok = string.Join(",", actual) == string.Join(",", expected);
            Console.WriteLine("[rngc2] " + name + " rangeLevels=" + rangeLevels + " picks=" + string.Join(",", actual));
            Console.WriteLine("[rngc2]   land-range expected=" + string.Join(",", expected) + (ok ? "  OK" : "  MISMATCH"));
            if (!ok) failures++;
        }

        // The rules ask the surface for the mode's district definition and use it for the tile
        // pre-filter and the score: the single hardcoded camp read is gone.
        var modes = new[] { ArmyActionType.CreateCamp, ArmyActionType.FoundCity, ArmyActionType.FoundCityWithSettler };
        surface.DefinitionRequests = "";
        for (int m = 0; m < modes.Length; m++)
            computation.Compute(CursorContext.For(hovered, "army", 0, 2, 1, 2, modes[m]));
        string expectedRequests = "CreateCamp=camp\nFoundCity=cityCenter\nFoundCityWithSettler=cityCenter\n";
        bool perMode = surface.DefinitionRequests == expectedRequests;
        Console.WriteLine("[rngc2]   definitions per mode=" + surface.DefinitionRequests.Replace("\n", " ") + (perMode ? " OK" : " MISMATCH"));
        if (!perMode) failures++;

        return failures;
    }

    private static int Tile(string entry) { return int.Parse(entry.Split('/')[1]); }

    private static int[] IndexesOf(GraphSurface surface, int territory)
    {
        var edges = surface.AdjacentTerritories(territory);
        var indexes = new int[edges.Length];
        for (int i = 0; i < edges.Length; i++) indexes[i] = edges[i].TerritoryIndex;
        Array.Sort(indexes);
        return indexes;
    }

    private static GraphSurface Grid(int count)
    {
        var surface = new GraphSurface { TerritoryCount = count };
        surface.IsOcean = new bool[count];
        surface.UnknownFacts = new bool[count];
        surface.Edges = new List<GraphSurface.Edge>[count];
        for (int i = 0; i < count; i++) surface.Edges[i] = new List<GraphSurface.Edge>();
        return surface;
    }

    private static void Link(GraphSurface surface, int a, int b, TerritoryTransition transition)
    {
        surface.Edges[a].Add(new GraphSurface.Edge { To = b, Transition = transition });
        surface.Edges[b].Add(new GraphSurface.Edge { To = a, Transition = transition });
    }
}
