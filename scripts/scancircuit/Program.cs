using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Amplitude;
using Amplitude.Mercury.Interop;
using SDStopwatch = System.Diagnostics.Stopwatch;

// PROTOTYPE (wayfinder ticket 10, throwaway, never merged).
// Question: order-of-magnitude cost of one cached placement-neighborhood scan pass
// (ticket 5 rules: hovered territory + RangeLevels hops over ALL adjacency edges, every
// tile; cheap pre-filters; mode gate reflection call per surviving tile; FIMS scoring;
// per-territory admission quota + tie set; per-territory cache; no cap, no budget).
// Cases: cold pass (cache miss), cache-hit refresh (mouse move: hits + newly entered
// territories), claim-triggered recompute (TerritoryInfo.Frame bump clears every entry).
//
// The gate is NOT the real gate: it needs a live Simulation world (Sandbox, World,
// VisibilityController, empires). The probe measures the reflection-invoke overhead on the
// exact gate signature against two stub bodies (light, heavy-with-settle-cost) and the real
// array/sort/cache work on synthetic graphs. Runtime caveat: net10 here, the game is Unity
// Mono (net472): Mono reflection is slower, treat numbers as a floor.
//
// Graph: 20x10 territory grid (200 = realistic big map; ceiling is 256 = 8-bit territory
// index, 65535 tiles). Base edges = Moore 8 (all-edge adjacency: land + sea + cliff +
// mountain, no TerritoryTransition filter, per ticket 5 Q18). Overlay edges random to the
// target mean degree; two maritime-hub territories get degree 30 (worst-case hubs).

internal static class Program
{
    // ---------------------------------------------------------------- synthetic world

    const int GridW = 20, GridH = 10;
    const int MapTerritories = GridW * GridH;
    const int WaterPct = 40;

    static int s_tilesPer;
    static int s_mapTiles;
    static int[] s_adjStart;
    static int[] s_adjList;
    static int[] s_tileTerritory;
    static byte[] s_tileFlags;        // bit0 water, bit1 explored
    static int[] s_tileFims;          // 7 nibbles of yields, food at nibble 0
    static bool[] s_territoryAffordable;

    static int TileOf(int x, int y) { return y * GridW + x; }

    static void BuildWorld(int tilesPer, int meanDegree, int exploredPct, int affordablePct, int seed)
    {
        s_tilesPer = tilesPer;
        s_mapTiles = MapTerritories * tilesPer;

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

        s_adjStart = new int[MapTerritories + 1];
        int totalEdges = 0;
        for (int t = 0; t < MapTerritories; t++) totalEdges += edges[t].Count;
        s_adjList = new int[totalEdges];
        int cursor = 0;
        for (int t = 0; t < MapTerritories; t++)
        {
            s_adjStart[t] = cursor;
            for (int k = 0; k < edges[t].Count; k++) s_adjList[cursor++] = edges[t][k];
        }
        s_adjStart[MapTerritories] = cursor;

        s_tileTerritory = new int[s_mapTiles];
        s_tileFlags = new byte[s_mapTiles];
        s_tileFims = new int[s_mapTiles];
        for (int tile = 0; tile < s_mapTiles; tile++)
        {
            int terr = tile / tilesPer;
            s_tileTerritory[tile] = terr;
            byte flags = 0;
            if (rng.Next(100) < WaterPct) flags |= 1;
            if (rng.Next(100) < exploredPct) flags |= 2;
            s_tileFlags[tile] = flags;
            int fims = 0;
            for (int r = 0; r < 7; r++) fims |= rng.Next(0, 7) << (r * 4);
            s_tileFims[tile] = fims;
        }

        s_territoryAffordable = new bool[MapTerritories];
        for (int t = 0; t < MapTerritories; t++) s_territoryAffordable[t] = rng.Next(100) < affordablePct;
    }

    // ---------------------------------------------------------------- gate stubs

    // Signature mirrors ArmyActionHelper.FillCreateCampFailures(army, tileIndex, ref flags,
    // ref money, ref culture, ref units) (ArmyActionHelper.cs:465). Army is internal and
    // non-constructable headless: object stands in.
    static ArmyActionFailureFlags s_none = ArmyActionFailureFlags_Part1.None;
    static int s_sink;

    static void GateLight(object army, int tileIndex, ref ArmyActionFailureFlags failureFlags,
        ref FixedPoint money, ref FixedPoint culture, ref FixedPoint units)
    {
        failureFlags = s_none;
        money = FixedPoint.Zero;
        culture = FixedPoint.Zero;
        units = FixedPoint.Zero;
    }

    // Heavy body: the work the tile gate actually does. FillCommonArmyFailures + army-side
    // overload reads (array reads), territory facts (reads), the two cost rows: GetSettleCost
    // (RPN over empires: per-empire FP multiply/add, distance decay: ArmyActionHelper.cs:491-507,
    // DepartmentOfTheTreasury.cs:1015-1056) plus CanAfford compares, and the terrain/POI
    // reads. Vanilla recomputes the per-territory settle cost inside EVERY tile call.
    static readonly FixedPoint s_fp1 = FixedPoint.One;
    static readonly FixedPoint s_fp2 = FixedPoint.One + FixedPoint.One;

    static void GateHeavy(object army, int tileIndex, ref ArmyActionFailureFlags failureFlags,
        ref FixedPoint money, ref FixedPoint culture, ref FixedPoint units)
    {
        int acc = (int)(tileIndex * 2654435761L);
        for (int i = 0; i < 40; i++) acc += s_tileFlags[(tileIndex + i) % s_mapTiles] + i;  // army/tile scratch reads
        int terr = s_tileTerritory[tileIndex];
        acc += s_tileTerritory[terr] + s_tileFlags[terr];                                    // territory facts

        FixedPoint cost = s_fp1;
        FixedPoint decay = s_fp1;
        for (int e = 0; e < 8; e++)
        {
            decay = decay * s_fp2 / s_fp2;                                                  // distance decay per empire
            cost = cost + decay * s_fp1;                                                    // belief influence contribution
        }
        money = cost;
        culture = cost;
        units = FixedPoint.Zero;
        if (cost > s_fp2) acc++;                                                            // CanAfford compares
        if (culture > s_fp2) acc++;
        failureFlags = s_none;
        s_sink += acc;
    }

    // Per-territory settle cost precheck (memoized affordability filter): GetSettleCost +
    // CanAfford once per territory (DepartmentOfTheTreasury.cs:1015, :789).
    static void SettleCostPrecheck(int territoryIndex, ref FixedPoint cost)
    {
        FixedPoint c = s_fp1;
        FixedPoint decay = s_fp1;
        for (int e = 0; e < 8; e++)
        {
            decay = decay * s_fp2 / s_fp2;
            c = c + decay * s_fp1;
        }
        cost = c;
        s_sink += territoryIndex;
    }

    // ---------------------------------------------------------------- neighborhood + scan

    static List<int> Neighborhood(int hovered, int rangeLevels)
    {
        var level = new int[MapTerritories];
        for (int i = 0; i < MapTerritories; i++) level[i] = -1;
        var result = new List<int>();
        var frontier = new List<int> { hovered };
        level[hovered] = 1;
        result.Add(hovered);
        int current = 1;
        while (frontier.Count > 0 && current < rangeLevels)
        {
            current++;
            var next = new List<int>();
            for (int i = 0; i < frontier.Count; i++)
            {
                int t = frontier[i];
                for (int k = s_adjStart[t]; k < s_adjStart[t + 1]; k++)
                {
                    int n = s_adjList[k];
                    if (level[n] != -1) continue;
                    level[n] = current;
                    next.Add(n);
                    result.Add(n);
                }
            }
            frontier = next;
        }
        return result;
    }

    static long TileScore(int tile)
    {
        int fims = s_tileFims[tile];
        long score = 0;
        for (int r = 0; r < 7; r++)
        {
            int yield = (fims >> (r * 4)) & 15;
            if (r == 0) score += yield * 3;      // Food 1.5
            else if (r == 1) score += yield * 2; // Industry 1.0
        }
        return score;
    }

    struct ScanResult
    {
        public int NbTiles;
        public int Survivors;
        public double TraverseMs;
        public double PrefilterMs;
        public double GateMs;
        public double ScoreSortMs;
        public double CacheMs;
        public double TotalMs;
    }

    static readonly MethodInfo s_gateHeavyMethod =
        typeof(Program).GetMethod("GateHeavy", BindingFlags.NonPublic | BindingFlags.Static);
    static readonly MethodInfo s_gateLightMethod =
        typeof(Program).GetMethod("GateLight", BindingFlags.NonPublic | BindingFlags.Static);
    static readonly MethodInfo s_costMethod =
        typeof(Program).GetMethod("SettleCostPrecheck", BindingFlags.NonPublic | BindingFlags.Static);

    // One full scan pass over the territory list (neighborhood on a cold refresh, a subset on
    // a cache-hit refresh). Writes cache entries for every scanned territory.
    static ScanResult ScanPass(List<int> territories, Dictionary<int, int[]> cache,
        bool memoizeCost, int topCount, MethodInfo gate, object army)
    {
        var r = new ScanResult();

        var sw = SDStopwatch.StartNew();
        var tiles = new List<int>(territories.Count * s_tilesPer);
        for (int i = 0; i < territories.Count; i++)
        {
            int baseTile = territories[i] * s_tilesPer;
            for (int k = 0; k < s_tilesPer; k++) tiles.Add(baseTile + k);
        }
        r.NbTiles = tiles.Count;
        sw.Stop(); r.TraverseMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        var survivors = new List<int>(tiles.Count / 4);
        for (int i = 0; i < tiles.Count; i++)
        {
            byte flags = s_tileFlags[tiles[i]];
            if ((flags & 1) != 0) continue;              // water
            if ((flags & 2) == 0) continue;              // unexplored
            survivors.Add(tiles[i]);
        }
        r.Survivors = survivors.Count;
        sw.Stop(); r.PrefilterMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        var affordable = new bool[MapTerritories];
        if (memoizeCost)
        {
            for (int i = 0; i < territories.Count; i++)
            {
                var cargs = new object[2] { territories[i], FixedPoint.Zero };
                s_costMethod.Invoke(null, cargs);
                affordable[territories[i]] = (FixedPoint)cargs[1] <= s_fp2;
            }
        }
        else
        {
            for (int i = 0; i < MapTerritories; i++) affordable[i] = true;
        }
        var valid = new List<int>(survivors.Count);
        for (int i = 0; i < survivors.Count; i++)
        {
            int tile = survivors[i];
            if (!affordable[s_tileTerritory[tile]]) continue;    // territory-level affordability filter
            var args = new object[6];
            args[0] = army;
            args[1] = tile;
            args[2] = s_none;
            args[3] = FixedPoint.Zero;
            args[4] = FixedPoint.Zero;
            args[5] = FixedPoint.Zero;
            gate.Invoke(null, args);
            var flags = (ArmyActionFailureFlags)args[2];
            if (flags == s_none) valid.Add(tile);
        }
        sw.Stop(); r.GateMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        var buckets = new List<int>[territories.Count];
        var bucketOf = new Dictionary<int, int>(territories.Count);
        for (int i = 0; i < territories.Count; i++)
        {
            buckets[i] = new List<int>();
            bucketOf[territories[i]] = i;
        }
        for (int i = 0; i < valid.Count; i++)
            buckets[bucketOf[s_tileTerritory[valid[i]]]].Add(valid[i]);

        var adoptedPerTerritory = new int[territories.Count][];
        for (int i = 0; i < territories.Count; i++)
        {
            var bucket = buckets[i];
            var keys = new long[bucket.Count];
            for (int k = 0; k < bucket.Count; k++)
                keys[k] = ((long)(100000 - TileScore(bucket[k])) << 20) | (long)(uint)bucket[k];
            Array.Sort(keys);
            int admitted = Math.Min(topCount, keys.Length);
            if (admitted > 0)
            {
                long cut = keys[admitted - 1] >> 20;
                int j2 = admitted;
                while (j2 < keys.Length && (keys[j2] >> 20) == cut) { admitted++; j2++; }   // full tie set
            }
            var own = new int[admitted];
            for (int k = 0; k < admitted; k++) own[k] = (int)(keys[k] & ((1 << 20) - 1));
            Array.Sort(own);                                                    // writer order: ascending tile index
            adoptedPerTerritory[i] = own;
        }
        sw.Stop(); r.ScoreSortMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        for (int i = 0; i < territories.Count; i++) cache[territories[i]] = adoptedPerTerritory[i];
        sw.Stop(); r.CacheMs = sw.Elapsed.TotalMilliseconds;

        r.TotalMs = r.TraverseMs + r.PrefilterMs + r.GateMs + r.ScoreSortMs + r.CacheMs;
        return r;
    }

    static double MedianOf(int reps, Func<ScanResult> measure)
    {
        var samples = new double[reps];
        for (int i = 0; i < reps; i++) samples[i] = measure().TotalMs;
        Array.Sort(samples);
        return samples[reps / 2];
    }

    struct Scenario
    {
        public string Name;
        public int TilesPer, Degree, Range, ExploredPct, AffordablePct;
        public bool MemoizeCost;
        public int Hovered;
    }

    public static void Main()
    {
        var ci = CultureInfo.InvariantCulture;

        BuildWorld(48, 10, 40, 100, 12345);
        var army = new object();
        var sw = SDStopwatch.StartNew();

        Func<int, int, Action, MethodInfo, double> bench = null;
        bench = delegate(int reps, int warm, Action body, MethodInfo unused)
        {
            for (int i = 0; i < warm; i++) body();
            sw.Restart();
            for (int i = 0; i < reps; i++) body();
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds * 1e6 / reps;
        };

        Action light = delegate { ReflectGateOnce(army, 4242, s_gateLightMethod); };
        Action heavy = delegate { ReflectGateOnce(army, 4242, s_gateHeavyMethod); };
        Action direct = delegate
        {
            var f = s_none; var a = FixedPoint.Zero; var b = FixedPoint.Zero; var c = FixedPoint.Zero;
            GateHeavy(army, 4242, ref f, ref a, ref b, ref c);
        };
        Action cost = delegate
        {
            var args = new object[2] { 42, FixedPoint.Zero };
            s_costMethod.Invoke(null, args);
        };

        double lightNs = bench(20000, 4000, light, null);
        double heavyNs = bench(20000, 4000, heavy, null);
        double directNs = bench(20000, 4000, direct, null);
        double costNs = bench(20000, 4000, cost, null);

        Console.WriteLine("PROTOTYPE ticket 10: cached placement-neighborhood scan, net10 (game runs Mono: treat as floor)");
        Console.WriteLine();
        Console.WriteLine("per-call costs (ns/call, warm):");
        Console.WriteLine(string.Format(ci,
            "  gate reflection, light body: {0:F0}\n  gate reflection, settle-cost body: {1:F0}" +
            "\n  gate direct call, settle-cost body: {2:F0}\n  per-territory settle cost precheck (reflection): {3:F0}",
            lightNs, heavyNs, directNs, costNs));
        Console.WriteLine();

        var scenarios = new[]
        {
            new Scenario { Name = "baseline map        T48 D10 R2 e40", TilesPer = 48, Degree = 10, Range = 2, ExploredPct = 40, AffordablePct = 100, Hovered = TileOf(5, 2) },
            new Scenario { Name = "hub territory       T48 D10 R2 e40", TilesPer = 48, Degree = 10, Range = 2, ExploredPct = 40, AffordablePct = 100, Hovered = TileOf(10, 5) },
            new Scenario { Name = "dense map           T96 D16 R2 e40", TilesPer = 96, Degree = 16, Range = 2, ExploredPct = 40, AffordablePct = 100, Hovered = TileOf(5, 2) },
            new Scenario { Name = "early game fog      T48 D10 R2 e10", TilesPer = 48, Degree = 10, Range = 2, ExploredPct = 10, AffordablePct = 100, Hovered = TileOf(5, 2) },
            new Scenario { Name = "wide scan           T48 D10 R3 e40", TilesPer = 48, Degree = 10, Range = 3, ExploredPct = 40, AffordablePct = 100, Hovered = TileOf(5, 2) },
            new Scenario { Name = "dense + cost memo   T96 D16 R2 e40 U50", TilesPer = 96, Degree = 16, Range = 2, ExploredPct = 40, AffordablePct = 50, MemoizeCost = true, Hovered = TileOf(5, 2) },
        };

        Console.WriteLine("per-refresh scan cost (ms, median of 5):");
        Console.WriteLine(string.Format(ci, "{0}  nb-terr  nb-tiles  gates   cold    hit     recompute", "scenario"));
        foreach (var s in scenarios)
        {
            BuildWorld(s.TilesPer, s.Degree, s.ExploredPct, s.AffordablePct, 987);
            var nb = Neighborhood(s.Hovered, s.Range);

            double cold = MedianOf(5, delegate
            {
                var c = new Dictionary<int, int[]>();
                return ScanPass(nb, c, s.MemoizeCost, 2, s_gateHeavyMethod, army);
            });

            // Cache-hit refresh: hover moves to a neighbor; cached neighborhoods serve hits,
            // newly entered territories scan cold.
            var cacheA = new Dictionary<int, int[]>();
            ScanPass(nb, cacheA, s.MemoizeCost, 2, s_gateHeavyMethod, army);
            var nbB = Neighborhood((s.Hovered + 1) % MapTerritories, s.Range);
            var fresh = new List<int>();
            for (int i = 0; i < nbB.Count; i++) if (!cacheA.ContainsKey(nbB[i])) fresh.Add(nbB[i]);
            sw.Restart();
            int hits = 0;
            for (int i = 0; i < nbB.Count; i++) { int[] p; if (cacheA.TryGetValue(nbB[i], out p)) hits++; }
            sw.Stop();
            double lookupMs = sw.Elapsed.TotalMilliseconds;
            var freshList = fresh.Count > 0 ? fresh : new List<int> { (s.Hovered + 1) % MapTerritories };
            double freshScan = MedianOf(5, delegate
            {
                var c = new Dictionary<int, int[]>();
                return ScanPass(freshList, c, s.MemoizeCost, 2, s_gateHeavyMethod, army);
            });
            double hit = lookupMs + freshScan;

            double recompute = cold;   // claim bump: cache fully invalid, same full pass

            int gates = 0;
            for (int i = 0; i < nb.Count; i++)
            {
                for (int k = 0; k < s_tilesPer; k++)
                {
                    int tile = nb[i] * s_tilesPer + k;
                    byte flags = s_tileFlags[tile];
                    if ((flags & 1) != 0 || (flags & 2) == 0) continue;
                    if (s.MemoizeCost && !s_territoryAffordable[nb[i]]) continue;
                    gates++;
                }
            }
            Console.WriteLine(string.Format(ci,
                "{0}  {1,7}  {2,8}  {3,6}  {4,5:F1}  {5,5:F1}  {6,8:F1}",
                s.Name, nb.Count, nb.Count * s.TilesPer, gates, cold, hit, recompute));
        }

        Console.WriteLine();
        Console.WriteLine("hit = lookup all cached + scan newly entered territories after a hover move; recompute = full pass on an invalidated cache.");
        Console.WriteLine("sink=" + s_sink);
    }

    static void ReflectGateOnce(object army, int tile, MethodInfo gate)
    {
        var args = new object[6];
        args[0] = army;
        args[1] = tile;
        args[2] = s_none;
        args[3] = FixedPoint.Zero;
        args[4] = FixedPoint.Zero;
        args[5] = FixedPoint.Zero;
        gate.Invoke(null, args);
        s_sink += ((ArmyActionFailureFlags)args[2]).GetHashCode();
    }
}
