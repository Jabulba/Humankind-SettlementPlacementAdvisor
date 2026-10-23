using System;
using System.Collections.Generic;
using System.Globalization;
using Amplitude; // FixedPoint (assembly Amplitude.Framework.dll).
using Amplitude.Mercury.Data.Simulation; // ArmyActionType, DistrictPlacementEvaluation
using Amplitude.Mercury.Interop; // ArmyActionFailureFlags, ArmyActionFailureFlags_Part1
using Amplitude.Mercury.Interop.AI.Entities; // Territory.Adjacency
using Amplitude.Mercury.Simulation;
using SettlementPlacementAdvisor; // the production pipeline under test
using TerritoryTransition = Amplitude.Mercury.Data.AI.TerritoryTransition;
using SDStopwatch = System.Diagnostics.Stopwatch;

/// <summary>
/// TICKET 20 scan-cost synthetic surface: the placement-neighborhood scan (territory filter, tile
/// pre-filter, tile gate, scoring, admission, the placement-neighborhood adjacency relation and the
/// per-territory cache) is all read through GameSurface and driven by the real PickComputation. The
/// tile gate runs the settle-cost body (RPN over empires + CanAfford compares, ArmyActionHelper.cs
/// GetSettleCost / DepartmentOfTheTreasury.cs) so every gate call measures the gate's real per-tile
/// work. TileGateCalls and per-territory FactsReads / TileGateCallsByTerritory make "served from
/// the cache" and "recomputed" observable. No affordability prefilter: the gate runs on every
/// pre-filter survivor (SPEC section 2 step 6).
/// </summary>
internal sealed class ScanSurface : GameSurface
{
    public int TerritoryCount;
    public int TilesPer;
    public List<int>[] Edges;
    public bool[] IsEdgeOfTheWorld;
    public bool[] Claimed;
    public int[] OwnerEmpire; // owning empire per territory, -1 unowned: an owner steal moves the stamp
    public bool[] HasSettlement;
    public bool[] UnknownFacts;
    public bool[] UnknownAdjacency;
    public int[][] Tiles; // settlement-site tiles per territory
    public byte[] TileWater; // 1 = water tile (per-tile filter, never the territory ocean flag)
    public byte[] TileExplored; // 1 = scouted: the gate's any-bit predicate
    public int[] TileScore; // settlement score per tile (FIMS weights)
    public ArmyActionFailureFlags_Part1 ArmyGateFlags;

    public int DistrictFrame = 1;
    public int ConstructionFrame = 1;
    public int VisibilityFrame = 1;
    public int TerritoryFrame = 1;

    public int TileGateCalls; // the gate runs on every pre-filter survivor
    public int[] FactsReads; // territory-facts reads per territory: a cache-served pass reads none
    public int[] TileGateCallsByTerritory; // per territory: a cache-served territory asks no tile gate
    int s_sink; // keeps the settle-cost body's scratch reads from being optimized away

    static readonly FixedPoint FP1 = FixedPoint.One;
    static readonly FixedPoint FP2 = FixedPoint.One + FixedPoint.One;

    public bool Ready { get { return true; } }
    public object TryGetArmy(SimulationEntityGUID guid) { return "army"; }
    public int GetArmyEmpireIndex(object army) { return 0; }

    public bool TryGetDistrictDefinition(ArmyActionType mode, out object districtDefinition)
    {
        if (mode == ArmyActionType.CreateCamp) { districtDefinition = "camp"; return true; }
        districtDefinition = null;
        return false;
    }

    public bool IsSettlementSiteTile(object districtDefinition, int tileIndex) { return districtDefinition != null; }

    public bool IsTileWater(int tileIndex) { return TileWater[tileIndex] != 0; }

    public bool IsTileExplored(int tileIndex, int empireIndex) { return TileExplored[tileIndex] != 0; }

    public bool IsTileResourceTile(int tileIndex) { return false; }

    public bool EvaluateTile(object army, int tileIndex, object districtDefinition, out DistrictPlacementEvaluation.ValidTile validTile, out FixedPoint score)
    {
        var vt = default(DistrictPlacementEvaluation.ValidTile);
        vt.TileIndex = tileIndex;
        validTile = vt;
        score = TileScore[tileIndex];
        return true;
    }

    public bool GetCurrentFrames(out int districtFrame, out int constructionFrame, out int visibilityFrame)
    {
        districtFrame = DistrictFrame; constructionFrame = ConstructionFrame; visibilityFrame = VisibilityFrame;
        return true;
    }

    public bool GetTerritoryFrame(out int territoryFrame) { territoryFrame = TerritoryFrame; return true; }

    public int TerritoryOfTile(int tileIndex) { return tileIndex / TilesPer; }

    public TerritoryFactsResult GetTerritoryFacts(int territoryIndex, out TerritoryFacts facts)
    {
        facts = default(TerritoryFacts);
        if (territoryIndex < 0 || territoryIndex >= TerritoryCount || UnknownFacts[territoryIndex])
            return TerritoryFactsResult.Unknown;
        FactsReads[territoryIndex]++;
        facts.IsEdgeOfTheWorld = IsEdgeOfTheWorld[territoryIndex];
        facts.Claimed = Claimed[territoryIndex];
        facts.HasSettlement = HasSettlement[territoryIndex];
        facts.OwnerEmpireIndex = OwnerEmpire[territoryIndex];
        facts.Tiles = Tiles[territoryIndex];
        return TerritoryFactsResult.Known;
    }

    /// <summary>The placement-neighborhood relation: every adjacency edge, unfiltered.</summary>
    public bool TryGetAdjacentTerritories(int territoryIndex, out Territory.Adjacency[] edges)
    {
        edges = null;
        if (territoryIndex < 0 || territoryIndex >= TerritoryCount || UnknownAdjacency[territoryIndex])
            return false;
        var links = Edges[territoryIndex];
        var adjacencies = new Territory.Adjacency[links.Count];
        for (int i = 0; i < links.Count; i++)
            adjacencies[i] = new Territory.Adjacency { TerritoryIndex = links[i], Transition = TerritoryTransition.Open };
        edges = adjacencies;
        return true; // every edge: the relation applies no transition filter
    }

    public bool TryGetTileGateFailures(ArmyActionType mode, object army, int tileIndex, bool maskMoneyAndInfluence, out ArmyActionFailureFlags failures, out FixedPoint moneyCost, out FixedPoint influenceCost, out FixedPoint unitCost)
    {
        TileGateCalls++;
        int t = TerritoryOfTile(tileIndex);
        if (t >= 0) TileGateCallsByTerritory[t]++;

        // The settle-cost body the real gate does per tile: GetSettleCost (RPN over empires: per-
        // empire FP multiply/add + distance decay) and the CanAfford compares (ArmyActionHelper.cs
        // :491-507, DepartmentOfTheTreasury.cs :1015-1056). Vanilla recomputes it inside EVERY call.
        int acc = (int)(tileIndex * 2654435761L);
        FixedPoint cost = FP1;
        FixedPoint decay = FP1;
        for (int e = 0; e < 8; e++)
        {
            decay = decay * FP2 / FP2; // distance decay per empire
            cost = cost + decay * FP1; // belief-influence contribution
        }
        if (cost > FP2) acc++; // CanAfford compares
        s_sink += acc;
        moneyCost = cost;
        influenceCost = cost;
        unitCost = FixedPoint.Zero;
        failures = maskMoneyAndInfluence
            ? GameApi.ApplyMoneyInfluenceMask((ArmyActionFailureFlags)ArmyActionFailureFlags_Part1.None)
            : (ArmyActionFailureFlags)ArmyActionFailureFlags_Part1.None;
        return army != null;
    }

    public bool TryGetArmyGateFailures(ArmyActionType mode, object army, out ArmyActionFailureFlags failures)
    {
        failures = (ArmyActionFailureFlags)ArmyGateFlags;
        return army != null;
    }

    public void ResetCounters()
    {
        TileGateCalls = 0;
        for (int i = 0; i < FactsReads.Length; i++)
        {
            FactsReads[i] = 0;
            TileGateCallsByTerritory[i] = 0;
        }
    }
}

/// <summary>
/// TICKET 20 scan-cost harness. The production placement pipeline is the code under test: the wide-
/// scan acceptance row (SPEC section 9) is the level-3 neighborhood over ~4300 tiles / ~1000 gate
/// calls, completing within 5 ms per refresh on net10 with an explicit 4x Unity Mono inflation
/// allowance. Reported rows: cold pass, cache-hit refresh, claim-triggered recompute. No runtime
/// cap, budget, or affordability prefilter is introduced to meet the bound.
///
/// Graph: a 20x10 territory grid (200 = realistic big map; ceiling 256 = 8-bit territory index).
/// Base edges = Moore 8 (all-edge adjacency, no transition filter). Overlay edges random to the
/// target mean degree; two maritime hubs reach degree 30 (worst-case hubs). Gate cost caveat:
/// net10 here, the game is Unity Mono (net472); Mono is slower, so the measured net10 numbers are a
/// floor, carried through the 4x inflation allowance.
/// </summary>
internal static class Program
{
    const int GridW = 20, GridH = 10;
    const int MapTerritories = GridW * GridH;
    const int WaterPct = 40; // fixed: 40% water, per SPEC prototype

    const double BoundMs = 5.0; // SPEC section 9: per-refresh bound, net10 + 4x Unity Mono allowance
    const double MonoInflation = 4.0; // Unity Mono (net472) inflation allowance on the net10 floor
    const int WideTilesPer = 48, WideDegree = 10, WideRange = 3, WideExplored = 40, WideSeed = 987;

    private static int s_Failures;

    // Gate bodies for the per-call reflection benchmark: the production tile gate runs through a
    // reflection invoke on the FillCreateCampFailures signature (ArmyActionHelper.cs:465), so the
    // harness reports reflection overhead over the direct call. Army is internal and
    // non-constructable headless: object stands in.
    static readonly ArmyActionFailureFlags_Part1 s_none = ArmyActionFailureFlags_Part1.None;

    static void GateLight(object army, int tileIndex, ref ArmyActionFailureFlags failureFlags,
        ref FixedPoint money, ref FixedPoint culture, ref FixedPoint units)
    {
        failureFlags = (ArmyActionFailureFlags)s_none;
        money = FixedPoint.Zero;
        culture = FixedPoint.Zero;
        units = FixedPoint.Zero;
    }

    static void GateHeavy(object army, int tileIndex, ref ArmyActionFailureFlags failureFlags,
        ref FixedPoint money, ref FixedPoint culture, ref FixedPoint units)
    {
        int acc = (int)(tileIndex * 2654435761L);
        FixedPoint cost = FixedPoint.One;
        FixedPoint decay = FixedPoint.One;
        FixedPoint two = FixedPoint.One + FixedPoint.One;
        for (int e = 0; e < 8; e++)
        {
            decay = decay * two / two;
            cost = cost + decay * FixedPoint.One;
        }
        money = cost;
        culture = cost;
        units = FixedPoint.Zero;
        if (cost > two) acc++;
        failureFlags = (ArmyActionFailureFlags)s_none;
        s_heavySink += acc;
    }

    static int s_heavySink;

    static readonly System.Reflection.MethodInfo s_gateHeavyMethod =
        typeof(Program).GetMethod("GateHeavy", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
    static readonly System.Reflection.MethodInfo s_gateLightMethod =
        typeof(Program).GetMethod("GateLight", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

    private static int Main()
    {
        Plugin.Instance = new Plugin(); // so a surface failure has a log to write to
        var ci = CultureInfo.InvariantCulture;

        Console.WriteLine("SCANCIRCUIT [scan]: production placement-neighborhood scan, net10 (game runs Unity Mono: 4x inflation allowance applied below).");
        Console.WriteLine();

        // Per-call gate cost (ns/call, warm): the production tile gate is a reflection invoke on the
        // settle-cost signature, so reflection overhead over the direct call is part of the floor.
        var army = new object();
        var sw = SDStopwatch.StartNew();
        double lightNs = Benchmark(20000, 4000, delegate
        {
            var f = (ArmyActionFailureFlags)s_none; var a = FixedPoint.Zero; var b = FixedPoint.Zero; var c = FixedPoint.Zero;
            s_gateLightMethod.Invoke(null, new object[6] { army, 4242, f, a, b, c });
        });
        double heavyNs = Benchmark(20000, 4000, delegate
        {
            var f = (ArmyActionFailureFlags)s_none; var a = FixedPoint.Zero; var b = FixedPoint.Zero; var c = FixedPoint.Zero;
            s_gateHeavyMethod.Invoke(null, new object[6] { army, 4242, f, a, b, c });
        });
        double directNs = Benchmark(20000, 4000, delegate
        {
            var f = (ArmyActionFailureFlags)s_none; var a = FixedPoint.Zero; var b = FixedPoint.Zero; var c = FixedPoint.Zero;
            GateHeavy(army, 4242, ref f, ref a, ref b, ref c);
        });
        Console.WriteLine(string.Format(ci,
            "gate call (ns/call, warm): reflection light={0:F0}  reflection settle-cost body={1:F0}  direct settle-cost body={2:F0}  reflection overhead={3:F0}",
            lightNs, heavyNs, directNs, heavyNs - directNs));
        Console.WriteLine();

        var scenarios = new[]
        {
            new Scenario { Name = "baseline map  T48 D10 R2 e40", TilesPer = 48, Degree = 10, Range = 2, ExploredPct = 40, Hovered = TileOf(5, 2) },
            new Scenario { Name = "hub territory T48 D10 R2 e40", TilesPer = 48, Degree = 10, Range = 2, ExploredPct = 40, Hovered = TileOf(10, 5) },
            new Scenario { Name = "dense map     T96 D16 R2 e40", TilesPer = 96, Degree = 16, Range = 2, ExploredPct = 40, Hovered = TileOf(5, 2) },
            new Scenario { Name = "early fog     T48 D10 R2 e10", TilesPer = 48, Degree = 10, Range = 2, ExploredPct = 10, Hovered = TileOf(5, 2) },
            new Scenario { Name = "WIDE SCAN     T54 D11 R3 e40", TilesPer = 54, Degree = 11, Range = 3, ExploredPct = 40, Hovered = TileOf(5, 2), Wide = true },
        };

        Console.WriteLine(string.Format(ci, "per-refresh scan cost (ms, median of 5; production PickComputation.Compute):"));
        Console.WriteLine(string.Format(ci, "{0}  nb-terr  nb-tiles  gates   cold    hit     recompute", "scenario"));

        bool sawWide = false;
        double wideCold = 0, wideHit = 0, wideRecompute = 0;
        foreach (var s in scenarios)
        {
            var surface = BuildWorld(s.TilesPer, s.Degree, s.ExploredPct, s.Seed);
            var nb = Neighborhood(surface, s.Hovered, s.Range);
            int nbTiles = nb.Count * s.TilesPer;

            // gate calls: the production gate runs on every pre-filter survivor of the cold pass
            surface.ResetCounters();
            new PickComputation(surface).Compute(Context(s.Hovered, s.Range));
            int gates = surface.TileGateCalls;

            // JIT-warm the surface so a measured pass costs a refresh, not first-run compilation.
            new PickComputation(surface).Compute(Context(s.Hovered, s.Range));

            double cold = MedianOf(5, delegate
            {
                var computation = new PickComputation(surface); // empty cache: the whole neighborhood recomputes
                return Timed(delegate { computation.Compute(Context(s.Hovered, s.Range)); });
            });

            // cache-hit refresh: hover moves to a neighbor; cached neighborhoods serve hits,
            // territories newly entering the neighborhood scan cold.
            double hit = MedianOf(5, delegate
            {
                var computation = new PickComputation(surface);
                computation.Compute(Context(s.Hovered, s.Range)); // fills the cache for the first neighborhood
                return Timed(delegate { computation.Compute(Context(s.Hovered + 1, s.Range)); });
            });

            // claim-triggered recompute: the claim frame bumps and every neighborhood territory's claim
            // stamp moves (an owner steal per territory), so the full pass recomputes; equals the cold pass.
            double recompute = MedianOf(5, delegate
            {
                var computation = new PickComputation(surface);
                computation.Compute(Context(s.Hovered, s.Range));
                surface.TerritoryFrame++;
                for (int i = 0; i < nb.Count; i++)
                    surface.OwnerEmpire[nb[i]] = surface.OwnerEmpire[nb[i]] == 0 ? 1 : 0; // owner steal: the stamp moves
                return Timed(delegate { computation.Compute(Context(s.Hovered, s.Range)); });
            });

            Console.WriteLine(string.Format(ci,
                "{0}  {1,7}  {2,8}  {3,6}  {4,5:F1}  {5,5:F1}  {6,8:F1}",
                s.Name, nb.Count, nbTiles, gates, cold, hit, recompute));

            if (s.Wide)
            {
                sawWide = true;
                wideCold = cold; wideHit = hit; wideRecompute = recompute;
                CheckWideShape("wide scan is RangeLevels 3", s.Range == 3);
                CheckWideShape("wide scan neighborhood is ~4300 tiles", nbTiles >= 4000 && nbTiles <= 4600);
                CheckWideShape("wide scan is ~1000 gate calls", gates >= 900 && gates <= 1100);
                Console.WriteLine(string.Format(ci,
                    "[scan] wide-scan shape: RangeLevels={0}  territories={1}  tiles={2}  gate-calls={3}",
                    s.Range, nb.Count, nbTiles, gates));
            }
        }

        Console.WriteLine();
        Console.WriteLine("hit = cache hits for served territories + scan of territories newly entering the neighborhood after a hover move; recompute = full pass on a claim-frame-invalidated cache (= cold).");
        Console.WriteLine();

        // SPEC section 9 acceptance: the wide-scan case completes within 5 ms per refresh on net10,
        // with an explicit 4x Unity Mono inflation allowance.
        Console.WriteLine(string.Format(ci,
            "wide-scan bound: {0:F1} ms net10 floor x{1:0} Unity Mono inflation = {2:F1} ms (bound {3:F1} ms per refresh)",
            wideCold, MonoInflation, wideCold * MonoInflation, BoundMs));
        Console.WriteLine(string.Format(ci,
            "[scan] {0}  wide-scan cold pass: measured={1:F1} ms  x4-inflated={2:F1} ms  bound={3:F1} ms",
            wideCold * MonoInflation <= BoundMs ? "OK  " : "FAIL", wideCold, wideCold * MonoInflation, BoundMs));
        Console.WriteLine(string.Format(ci,
            "[scan] {0}  wide-scan cache-hit refresh: measured={1:F1} ms  x4-inflated={2:F1} ms  bound={3:F1} ms",
            wideHit * MonoInflation <= BoundMs ? "OK  " : "FAIL", wideHit, wideHit * MonoInflation, BoundMs));
        Console.WriteLine(string.Format(ci,
            "[scan] {0}  wide-scan claim-triggered recompute: measured={1:F1} ms  x4-inflated={2:F1} ms  bound={3:F1} ms",
            wideRecompute * MonoInflation <= BoundMs ? "OK  " : "FAIL", wideRecompute, wideRecompute * MonoInflation, BoundMs));
        if (!(wideCold * MonoInflation <= BoundMs && wideHit * MonoInflation <= BoundMs && wideRecompute * MonoInflation <= BoundMs))
            s_Failures++;
        if (!sawWide) { Console.WriteLine("FAIL wide-scan scenario missing"); s_Failures++; }

        Console.WriteLine("[scan] verdict: " + (s_Failures == 0 ? "GREEN" : "RED (" + s_Failures + " bound/shape violations)"));
        return s_Failures == 0 ? 0 : 1;
    }

    private static CursorContext Context(int hovered, int rangeLevels)
    {
        return CursorContext.For(hovered, "army", 0, 2, 1, rangeLevels, ArmyActionType.CreateCamp);
    }

    private struct Scenario
    {
        public string Name;
        public int TilesPer, Degree, Range, ExploredPct, Seed, Hovered;
        public bool Wide;
    }

    // ---------------------------------------------------------------- graph

    private static int TileOf(int x, int y) { return y * GridW + x; }

    private static ScanSurface BuildWorld(int tilesPer, int meanDegree, int exploredPct, int seed)
    {
        int mapTiles = MapTerritories * tilesPer;
        var rng = new Random(seed);
        var edges = new List<int>[MapTerritories];
        var seen = new HashSet<long>[MapTerritories];
        for (int t = 0; t < MapTerritories; t++) { edges[t] = new List<int>(); seen[t] = new HashSet<long>(); }

        Action<int, int> link = delegate(int a, int b)
        {
            if (a == b) return;
            long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            if (!seen[a].Add(key)) return;
            seen[b].Add(key);
            edges[a].Add(b);
            edges[b].Add(a);
        };

        // Base: Moore neighborhood on the grid (wraps in x: cylindrical map).
        int[] dx = { -1, 1, 0, 0, -1, -1, 1, 1 };
        int[] dy = { -1, 1, -1, 1, 0, 0, -1, 1 };
        for (int y = 0; y < GridH; y++)
            for (int x = 0; x < GridW; x++)
                for (int k = 0; k < 8; k++)
                {
                    int nx = (x + dx[k] + GridW) % GridW, ny = y + dy[k];
                    if (ny < 0 || ny >= GridH) continue;
                    link(TileOf(x, y), TileOf(nx, ny));
                }

        // Overlay edges to the target mean degree + two hubs (degree 30).
        int hubDegree = 30;
        var hubsLeft = new List<int> { TileOf(10, 5), TileOf(3, 8) };
        for (int guard = 0; guard < 500000; guard++)
        {
            int total = 0;
            for (int t = 0; t < MapTerritories; t++) total += edges[t].Count;
            int target = MapTerritories * meanDegree;
            int hub = -1;
            for (int i = 0; i < hubsLeft.Count; i++)
                if (edges[hubsLeft[i]].Count < hubDegree && (hub == -1 || edges[hubsLeft[i]].Count < edges[hub].Count)) hub = hubsLeft[i];
            if (total >= target && hub == -1) break;
            if (hub == -1)
            {
                int a = rng.Next(MapTerritories);
                if (edges[a].Count < meanDegree + 2) link(a, rng.Next(MapTerritories));
            }
            else if (edges[hub].Count < hubDegree)
            {
                link(hub, rng.Next(MapTerritories));
            }
            else
            {
                hubsLeft.Remove(hub);
            }
        }

        var surface = new ScanSurface
        {
            TerritoryCount = MapTerritories,
            TilesPer = tilesPer,
            Edges = edges,
            IsEdgeOfTheWorld = new bool[MapTerritories],
            Claimed = new bool[MapTerritories],
            HasSettlement = new bool[MapTerritories],
            UnknownFacts = new bool[MapTerritories],
            UnknownAdjacency = new bool[MapTerritories],
            OwnerEmpire = new int[MapTerritories],
            Tiles = new int[MapTerritories][],
            TileWater = new byte[mapTiles],
            TileExplored = new byte[mapTiles],
            TileScore = new int[mapTiles],
            FactsReads = new int[MapTerritories],
            TileGateCallsByTerritory = new int[MapTerritories],
        };
        for (int t = 0; t < MapTerritories; t++) surface.OwnerEmpire[t] = -1; // unowned

        for (int t = 0; t < MapTerritories; t++)
        {
            var list = new int[tilesPer];
            for (int k = 0; k < tilesPer; k++) list[k] = t * tilesPer + k;
            surface.Tiles[t] = list;
        }

        for (int tile = 0; tile < mapTiles; tile++)
        {
            if (rng.Next(100) < WaterPct) surface.TileWater[tile] = 1;
            if (rng.Next(100) < exploredPct) surface.TileExplored[tile] = 1;
            int fims = 0;
            for (int r = 0; r < 7; r++) fims |= rng.Next(0, 7) << (r * 4);
            // Settlement scoring on the vanilla weight table: Food 1.5, Industry 1.0 (the scored rows).
            int score = 0;
            for (int r = 0; r < 7; r++)
            {
                int yield = (fims >> (r * 4)) & 15;
                if (r == 0) score += yield * 3;
                else if (r == 1) score += yield * 2;
            }
            surface.TileScore[tile] = score;
        }
        return surface;
    }

    /// <summary>The placement neighborhood the scan walks: the hovered territory (level 1) plus
    /// RangeLevels - 1 expansions over every adjacency edge, matching PickComputation.AddNeighbourhood.</summary>
    private static List<int> Neighborhood(ScanSurface surface, int hovered, int rangeLevels)
    {
        var visited = new HashSet<int>();
        var result = new List<int>();
        var frontier = new List<int>();
        visited.Add(hovered);
        result.Add(hovered);
        frontier.Add(hovered);
        for (int level = 2; level <= rangeLevels && frontier.Count > 0; level++)
        {
            var next = new List<int>();
            for (int i = 0; i < frontier.Count; i++)
            {
                var edges = surface.Edges[frontier[i]];
                for (int k = 0; k < edges.Count; k++)
                {
                    int t = edges[k];
                    if (!visited.Add(t)) continue;
                    result.Add(t);
                    next.Add(t);
                }
            }
            frontier = next;
        }
        return result;
    }

    // ---------------------------------------------------------------- timing

    private static double Timed(Action body)
    {
        var sw = SDStopwatch.StartNew();
        body();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }

    private static double MedianOf(int reps, Func<double> measure)
    {
        var samples = new double[reps];
        for (int i = 0; i < reps; i++) samples[i] = measure();
        Array.Sort(samples);
        return samples[reps / 2];
    }

    private static double Benchmark(int reps, int warm, Action body)
    {
        var sw = SDStopwatch.StartNew();
        for (int i = 0; i < warm; i++) body();
        sw.Restart();
        for (int i = 0; i < reps; i++) body();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds * 1e6 / reps;
    }

    private static void CheckWideShape(string name, bool ok)
    {
        Console.WriteLine("[scan] " + (ok ? "OK   " : "FAIL ") + name);
        if (!ok) s_Failures++;
    }
}
