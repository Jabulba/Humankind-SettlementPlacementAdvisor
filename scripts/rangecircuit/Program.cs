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
/// DEBUG [rngc3] synthetic surface for the camp pipeline: the territory filter, the tile
/// pre-filter, the tile gate, scoring, admission and the cache are all read through
/// GameSurface. Every territory has settlement-site tiles with literal scores set per case, so
/// expected admissions are spec literals, never recomputed from the rules.
/// </summary>
internal sealed class GraphSurface : GameSurface
{
    internal struct Edge
    {
        public int To;
        public TerritoryTransition Transition;
    }

    public int TerritoryCount;
    public List<Edge>[] Edges;
    public bool[] UnknownFacts; // territories whose snapshot facts are missing
    public bool[] IsEdgeOfTheWorld;
    public bool[] Claimed;
    public bool[] HasSettlement;
    public bool[] AllOcean; // territory ocean flags: never a filter
    public int[][] Tiles; // settlement-site tiles per territory
    public Dictionary<int, int> ScoreByTile = new Dictionary<int, int>();
    public HashSet<int> WaterTiles = new HashSet<int>();
    public HashSet<int> UnexploredTiles = new HashSet<int>();
    public HashSet<int> InvalidSiteTiles = new HashSet<int>();
    public HashSet<int> ResourceTiles = new HashSet<int>();
    public HashSet<int> BorderTiles = new HashSet<int>(); // vanilla IsTileAtTerritoryBorder would flag these
    public Dictionary<int, ArmyActionFailureFlags_Part1> TileGateFlags = new Dictionary<int, ArmyActionFailureFlags_Part1>();
    public ArmyActionFailureFlags_Part1 ArmyGateFlags;

    public int DistrictFrame = 1;
    public int ConstructionFrame = 1;
    public int VisibilityFrame = 1;
    public int TerritoryFrame = 1;

    public int TileGateCalls; // the gate must run on every pre-filter survivor
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

    public bool IsSettlementSiteTile(object districtDefinition, int tileIndex)
    {
        return districtDefinition != null && !InvalidSiteTiles.Contains(tileIndex);
    }

    public bool IsTileWater(int tileIndex) { return WaterTiles.Contains(tileIndex); }

    public bool IsTileExplored(int tileIndex, int empireIndex) { return !UnexploredTiles.Contains(tileIndex); }

    public bool IsTileResourceTile(int tileIndex) { return ResourceTiles.Contains(tileIndex); }

    public bool EvaluateTile(object army, int tileIndex, object districtDefinition, out DistrictPlacementEvaluation.ValidTile validTile, out FixedPoint score)
    {
        var vt = default(DistrictPlacementEvaluation.ValidTile);
        vt.TileIndex = tileIndex;
        validTile = vt;
        int declared;
        score = ScoreByTile.TryGetValue(tileIndex, out declared) ? declared : tileIndex;
        return true;
    }

    public bool GetCurrentFrames(out int districtFrame, out int constructionFrame, out int visibilityFrame)
    {
        districtFrame = DistrictFrame; constructionFrame = ConstructionFrame; visibilityFrame = VisibilityFrame;
        return true;
    }

    public bool GetTerritoryFrame(out int territoryFrame)
    {
        territoryFrame = TerritoryFrame;
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
        facts.IsEdgeOfTheWorld = IsEdgeOfTheWorld[territoryIndex];
        facts.Claimed = Claimed[territoryIndex];
        facts.HasSettlement = HasSettlement[territoryIndex];
        facts.OwnerEmpireIndex = -1;
        facts.Tiles = Tiles[territoryIndex];
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
        TileGateCalls++;
        var part1 = TileGateFlags.ContainsKey(tileIndex) ? TileGateFlags[tileIndex] : ArmyActionFailureFlags_Part1.None;
        failures = maskMoneyAndInfluence ? GameApi.ApplyMoneyInfluenceMask((ArmyActionFailureFlags)part1) : (ArmyActionFailureFlags)part1;
        moneyCost = FixedPoint.Zero;
        influenceCost = FixedPoint.Zero;
        unitCost = FixedPoint.Zero;
        return army != null;
    }

    public bool TryGetArmyGateFailures(ArmyActionType mode, object army, out ArmyActionFailureFlags failures)
    {
        failures = (ArmyActionFailureFlags)ArmyGateFlags;
        return army != null;
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
    private static int s_Failures;

    private static int Main()
    {
        Admission();
        Filters();
        Gate();
        Scoring();
        Cache();
        Surface();

        Console.WriteLine("[rngc3] verdict: " + (s_Failures == 0 ? "GREEN" : "RED (" + s_Failures + " rule violations)"));
        return s_Failures == 0 ? 0 : 1;
    }

    /// <summary>SPEC section 2 step 8: clamped quota plus the full top tie set, score desc, tile asc.</summary>
    private static void Admission()
    {
        // Worked example 1: quota 2 with 3 tiles tied at rank 1 publishes 3.
        var tied = Camp(tiles: 4, scores: new[] { 50, 50, 50, 50 });
        Expect("quota 2 + 3 tied at rank 1 publishes the full tie set", Pick(tied, 2, 1), 100, 101, 102, 103);

        // Worked example 2: quota 2, 1 tile at rank 1, 3 tiles tied at rank 2 publishes 4.
        var stepped = Camp(tiles: 4, scores: new[] { 90, 50, 50, 50 });
        Expect("quota 2 + 1 at rank 1 + 3 tied at rank 2 publishes 4", Pick(stepped, 2, 1), 100, 101, 102, 103);

        // The tie set stops at the first different score: the rank-2 group is not admitted.
        var twoGroups = Camp(tiles: 4, scores: new[] { 90, 90, 50, 50 });
        Expect("the tie set stops at the first lower score", Pick(twoGroups, 2, 1), 100, 101);

        // No tie at all: the quota alone decides, ordered by score desc then tile asc.
        var distinct = Camp(tiles: 4, scores: new[] { 10, 30, 20, 40 });
        Expect("quota 2 admits the top 2 by score, published ascending by tile", Pick(distinct, 2, 1), 101, 103);

        // A quota of 1 on a rank-1 tie admits every tied tile: no tie cap.
        Expect("quota 1 on a rank-1 tie admits all tied tiles", Pick(tied, 1, 1), 100, 101, 102, 103);
    }

    /// <summary>Mode territory filter + tile pre-filter (SPEC section 2 steps 4 and 5).</summary>
    private static void Filters()
    {
        var edge = Camp(tiles: 3, scores: new[] { 10, 20, 30 });
        edge.IsEdgeOfTheWorld[0] = true;
        Expect("IsEdgeOfTheWorld territory is dropped", Pick(edge, 2, 1));

        var claimed = Camp(tiles: 3, scores: new[] { 10, 20, 30 });
        claimed.Claimed[0] = true;
        Expect("Claimed territory is dropped", Pick(claimed, 2, 1));

        var site = Camp(tiles: 3, scores: new[] { 10, 20, 30 });
        site.HasSettlement[0] = true;
        Expect("a territory with a settlement (SettlementGUID != 0) is dropped", Pick(site, 2, 1));

        var ocean = Camp(tiles: 3, scores: new[] { 10, 20, 30 });
        ocean.AllOcean[0] = true; // sea-flagged territory with land sites
        Expect("a sea-flagged territory is never filtered: water is a per-tile fact", Pick(ocean, 2, 1), 101, 102);

        var water = Camp(tiles: 4, scores: new[] { 40, 30, 20, 10 });
        water.WaterTiles.Add(100);
        Expect("water tiles are dropped per tile", Pick(water, 2, 1), 101, 102);

        var fog = Camp(tiles: 4, scores: new[] { 40, 30, 20, 10 });
        fog.UnexploredTiles.Add(100);
        Expect("unexplored tiles are dropped", Pick(fog, 2, 1), 101, 102);

        var terrain = Camp(tiles: 4, scores: new[] { 40, 30, 20, 10 });
        terrain.InvalidSiteTiles.Add(100);
        Expect("terrain + POI failures drop the tile", Pick(terrain, 2, 1), 101, 102);

        var unknown = Camp(tiles: 3, scores: new[] { 10, 20, 30 });
        unknown.UnknownFacts[0] = true;
        Expect("an unknown territory fails closed", Pick(unknown, 2, 1));
    }

    /// <summary>Tile gate with the money/influence mask (SPEC section 2 step 6).</summary>
    private static void Gate()
    {
        var unaffordable = Camp(tiles: 3, scores: new[] { 30, 10, 30 });
        unaffordable.TileGateFlags[100] = ArmyActionFailureFlags_Part1.NotEnoughMoney;
        unaffordable.TileGateFlags[101] = ArmyActionFailureFlags_Part1.NotEnoughInfluence;
        // Rank 1 is the 30s: the unaffordable tile (100) ties the clean tile (102) there, and
        // the affordable-only tile 101 (score 10) is outside the quota 1 window.
        Expect("unaffordable tiles stay in the admission set", Pick(unaffordable, 1, 1), 100, 102);

        var blocked = Camp(tiles: 3, scores: new[] { 30, 20, 10 });
        blocked.TileGateFlags[100] = ArmyActionFailureFlags_Part1.TerrainNotValid;
        blocked.TileGateFlags[101] = ArmyActionFailureFlags_Part1.TileIsOccupied;
        Expect("every other gate failure drops the tile", Pick(blocked, 2, 1), 102);

        var mixed = Camp(tiles: 3, scores: new[] { 30, 20, 10 });
        mixed.TileGateFlags[100] = ArmyActionFailureFlags_Part1.NotEnoughMoney | ArmyActionFailureFlags_Part1.TrespassingStatus;
        Expect("money masked together with another flag keeps that flag: the tile is dropped", Pick(mixed, 2, 1), 101, 102);

        // No affordability prefilter: the gate call runs on every pre-filter survivor.
        var counted = Camp(tiles: 3, scores: new[] { 30, 20, 10 });
        counted.TileGateFlags[100] = ArmyActionFailureFlags_Part1.NotEnoughMoney;
        counted.TileGateCalls = 0;
        Pick(counted, 2, 1);
        Check("the gate call runs on every pre-filter survivor", counted.TileGateCalls == 3);

        // Army-level gate (SPEC section 2 step 2): any hit skips the scan entirely.
        var locked = Camp(tiles: 3, scores: new[] { 30, 20, 10 });
        locked.ArmyGateFlags = ArmyActionFailureFlags_Part1.ArmyLocked;
        locked.TileGateCalls = 0;
        Expect("an army-level gate hit skips the scan", Pick(locked, 2, 1));
        Check("an army-level gate hit asks the tile gate no tile", locked.TileGateCalls == 0);

        var nomadShort = Camp(tiles: 3, scores: new[] { 30, 20, 10 });
        nomadShort.ArmyGateFlags = ArmyActionFailureFlags_Part1.NotEnoughUnits;
        Expect("the nomad unit-count gate hit publishes nothing", Pick(nomadShort, 2, 1));

        // Money and influence are army/row-12 cost facts: the masked tile gate keeps them out
        // of the membership test, so the scan runs with them set on tiles.
        var costOnly = Camp(tiles: 3, scores: new[] { 30, 20, 10 });
        costOnly.TileGateFlags[100] = ArmyActionFailureFlags_Part1.NotEnoughMoney;
        costOnly.TileGateFlags[101] = ArmyActionFailureFlags_Part1.NotEnoughInfluence;
        Expect("cost-only failures keep every tile admissible", Pick(costOnly, 3, 1), 100, 101, 102);


    }

    /// <summary>Scoring (SPEC section 2 step 7): weights and the resource tile kept, floor and border gone.</summary>
    private static void Scoring()
    {
        // The vanilla resource-tile exclusion (SimulationEvaluator.cs:2399) stays.
        var resource = Camp(tiles: 3, scores: new[] { 90, 30, 20 });
        resource.ResourceTiles.Add(100);
        Expect("the top-scoring resource tile is excluded from admission", Pick(resource, 2, 1), 101, 102);

        // The score-25 floor (MinimalFimsForCampPlacement) is dropped: weak tiles are admitted.
        var weak = Camp(tiles: 3, scores: new[] { 3, 2, 1 });
        Expect("tiles below the dropped 25 floor are admitted", Pick(weak, 2, 1), 100, 101);

        // IsTileAtTerritoryBorder is dropped: border tiles rank and publish.
        var border = Camp(tiles: 3, scores: new[] { 90, 30, 20 });
        border.BorderTiles.Add(100);
        Expect("territory-border tiles are admitted (rule dropped)", Pick(border, 2, 1), 100, 101);

        // No distance tie-break: the tie set is admitted whichever tile is nearest to the army.
        var nearTie = Camp(tiles: 4, scores: new[] { 50, 50, 50, 1 });
        Expect("the rank-1 tie set is admitted whole, with no distance tie-break", Pick(nearTie, 2, 1), 100, 101, 102);
    }

    /// <summary>Cache key (SPEC section 2, cadence and cache): mode + World.TerritoryInfo.Frame.</summary>
    private static void Cache()
    {
        var surface = Camp(tiles: 3, scores: new[] { 30, 20, 10 });
        var computation = new PickComputation(surface); // the cache is per computation, like a session

        Expect("camp pass admits the quota plus ties", Pick(computation, surface, ArmyActionType.CreateCamp, 2, 1), 100, 101);

        // A claim with no frame bump is invisible: the frame is the invalidation signal.
        surface.Claimed[0] = true;
        Expect("a claim with no claim-frame bump serves the cache", Pick(computation, surface, ArmyActionType.CreateCamp, 2, 1), 100, 101);

        // The claim-frame bump recomputes: the territory filter now drops it.
        surface.TerritoryFrame++;
        Expect("a claim-frame bump recomputes the territory", Pick(computation, surface, ArmyActionType.CreateCamp, 2, 1));

        // Mode in the cache key: an unwired-mode pass on the same territory runs no rules and
        // must leave the camp entry exactly as it was - only the claim-frame bump recomputes.
        Expect("a mode the pipeline has not wired fails closed", Pick(computation, surface, ArmyActionType.FoundCity, 2, 1));
        Expect("an unwired-mode pass leaves the camp entry untouched", Pick(computation, surface, ArmyActionType.CreateCamp, 2, 1));
        surface.Claimed[0] = false;
        surface.TerritoryFrame++;
        Expect("the camp entry recomputes when the claim frame moves", Pick(computation, surface, ArmyActionType.CreateCamp, 2, 1), 100, 101);

        // Visibility, district and construction frames keep invalidating.
        surface.VisibilityFrame++;
        Expect("the visibility frame still invalidates", Pick(computation, surface, ArmyActionType.CreateCamp, 2, 1), 100, 101);
        surface.DistrictFrame++;
        Expect("the district frame still invalidates", Pick(computation, surface, ArmyActionType.CreateCamp, 2, 1), 100, 101);
        surface.ConstructionFrame++;
        Expect("the construction frame still invalidates", Pick(computation, surface, ArmyActionType.CreateCamp, 2, 1), 100, 101);
    }

    /// <summary>Surface rows the pipeline relies on: all edges reported, definition per mode.</summary>
    private static void Surface()
    {
        var fan = Camp(tiles: 3, scores: new[] { 10, 20, 30 });
        Link(fan, 0, 1, TerritoryTransition.Open);
        Link(fan, 0, 2, TerritoryTransition.Coast);
        Link(fan, 0, 3, TerritoryTransition.Cliffs);
        Link(fan, 0, 4, TerritoryTransition.Closed);
        var reported = IndexesOf(fan, 0);
        var expectedAll = fan.AllAdjacentIndexes(0);
        bool allEdges = reported.Length == expectedAll.Length;
        for (int i = 0; i < reported.Length && allEdges; i++)
            if (reported[i] != expectedAll[i]) allEdges = false;
        Console.WriteLine("[rngc3] adjacency surface reports=" + string.Join(",", reported)
            + " all edges=" + string.Join(",", expectedAll) + (allEdges ? "  OK" : "  MISMATCH"));
        if (!allEdges) s_Failures++;

        // Mode dispatch runs before any mode fact is read: only the wired mode reaches the
        // surface's district definition, and the rules never hardcode the camp definition.
        var modes = new[] { ArmyActionType.CreateCamp, ArmyActionType.FoundCity, ArmyActionType.FoundCityWithSettler };
        fan.DefinitionRequests = "";
        for (int m = 0; m < modes.Length; m++)
            new PickComputation(fan).Compute(CursorContext.For(0, "army", 0, 2, 1, 2, modes[m]));
        bool perMode = fan.DefinitionRequests == "CreateCamp=camp\n";
        Console.WriteLine("[rngc3] definitions asked per mode=" + fan.DefinitionRequests.Replace("\n", " ")
            + " expected=CreateCamp=camp" + (perMode ? "  OK" : "  MISMATCH"));
        if (!perMode) s_Failures++;

        // Unmapped placement modes fail closed: no picks, no writes.
        var unmapped = new PickComputation(fan).Compute(CursorContext.For(0, "army", 0, 2, 1, 2, ArmyActionType.Ransack));
        Console.WriteLine("[rngc3] unmapped mode (Ransack) picks=" + (unmapped == null ? "none" : unmapped.Length.ToString())
            + (unmapped == null ? "  OK" : "  MISMATCH"));
        if (unmapped != null) s_Failures++;
    }

    private static GraphSurface Camp(int tiles, int[] scores)
    {
        var surface = new GraphSurface { TerritoryCount = 5 };
        surface.IsEdgeOfTheWorld = new bool[5];
        surface.Claimed = new bool[5];
        surface.HasSettlement = new bool[5];
        surface.AllOcean = new bool[5];
        surface.UnknownFacts = new bool[5];
        surface.Edges = new List<GraphSurface.Edge>[5];
        surface.Tiles = new int[5][];
        for (int i = 0; i < 5; i++)
        {
            surface.Edges[i] = new List<GraphSurface.Edge>();
            var list = new int[tiles];
            for (int t = 0; t < tiles; t++) list[t] = 100 + i * 10 + t;
            surface.Tiles[i] = list;
        }
        for (int t = 0; t < tiles; t++) surface.ScoreByTile[100 + t] = scores[t];
        return surface;
    }

    private static Pick[] Pick(GraphSurface surface, int topHovered, int topNeighbour)
    {
        return Pick(new PickComputation(surface), surface, ArmyActionType.CreateCamp, topHovered, topNeighbour);
    }

    private static Pick[] Pick(PickComputation computation, GraphSurface surface, ArmyActionType mode, int topHovered, int topNeighbour)
    {
        return computation.Compute(CursorContext.For(0, "army", 0, topHovered, topNeighbour, 2, mode));
    }

    private static int[] TilesOf(Pick[] picks)
    {
        if (picks == null) return new int[0];
        var tiles = new int[picks.Length];
        for (int i = 0; i < picks.Length; i++) tiles[i] = picks[i].Tile;
        Array.Sort(tiles);
        return tiles;
    }

    private static void Expect(string name, Pick[] picks, params int[] expectedTiles)
    {
        Check(name, string.Join(",", TilesOf(picks)) == string.Join(",", expectedTiles));
        Console.WriteLine("[rngc3] " + name + " picks=" + string.Join(",", TilesOf(picks))
            + " expected=" + string.Join(",", expectedTiles));
    }

    private static void Check(string name, bool ok)
    {
        Console.WriteLine("[rngc3] " + (ok ? "OK   " : "FAIL ") + name);
        if (!ok) s_Failures++;
    }

    private static void Link(GraphSurface surface, int a, int b, TerritoryTransition transition)
    {
        surface.Edges[a].Add(new GraphSurface.Edge { To = b, Transition = transition });
        surface.Edges[b].Add(new GraphSurface.Edge { To = a, Transition = transition });
    }

    private static int[] IndexesOf(GraphSurface surface, int territory)
    {
        var edges = surface.AdjacentTerritories(territory);
        var indexes = new int[edges.Length];
        for (int i = 0; i < edges.Length; i++) indexes[i] = edges[i].TerritoryIndex;
        Array.Sort(indexes);
        return indexes;
    }
}
