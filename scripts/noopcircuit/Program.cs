using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Amplitude;
using Amplitude.Mercury; // WorldPosition (the snapshot cursor)
using Amplitude.Mercury.Data.Simulation; // ArmyActionType
using Amplitude.Mercury.Interop;
using Amplitude.Mercury.Interop.AI.Entities; // Territory.Adjacency
using Amplitude.Mercury.Simulation;
using SettlementPlacementAdvisor;
using TerritoryTransition = Amplitude.Mercury.Data.AI.TerritoryTransition;

/// <summary>
/// Synthetic surface: four territories of three settlement-site tiles, scored by tile index, so a
/// territory's best tiles are its last two. No game or sandbox involved.
/// </summary>
internal sealed class GraphSurface : GameSurface
{
    public bool ReadyFlag = true;
    public bool AllOcean; // C3: report every territory as sea-flagged
    public bool AllWater; // C2: report every tile as water
    public bool HasSettlement; // the hovered territory carries a settlement (settler filter)
    public int SettlementOwner = -1; // owning empire of that settlement, -1 unowned
    public bool SettlementIsCity; // that settlement is a city, not a camp/pre-camp (not replaceable)

    public bool Ready { get { return ReadyFlag; } }
    public object TryGetArmy(SimulationEntityGUID guid) { return "army"; }
    public int GetArmyEmpireIndex(object army) { return 0; }

    public bool TryGetDistrictDefinition(ArmyActionType mode, out object districtDefinition)
    {
        if (mode == ArmyActionType.CreateCamp) { districtDefinition = "camp"; return true; }
        if (mode == ArmyActionType.FoundCity || mode == ArmyActionType.FoundCityWithSettler) { districtDefinition = "cityCenter"; return true; }
        districtDefinition = null;
        return false;
    }

    public bool IsSettlementSiteTile(object districtDefinition, int tileIndex) { return districtDefinition != null; }
    public bool IsTileWater(int tileIndex) { return AllWater; }
    public bool IsTileExplored(int tileIndex, int empireIndex) { return true; }
    public bool IsTileResourceTile(int tileIndex) { return ResourceTiles.Contains(tileIndex); }

    public HashSet<int> ResourceTiles = new HashSet<int>();
    public Dictionary<int, int> ScoreByTile = new Dictionary<int, int>();
    public ArmyActionFailureFlags_Part1 ArmyGateFlags;
    public bool ThrowAtTheGate; // a pipeline that throws mid-pass: the takeover's catch path
    public Dictionary<int, ArmyActionFailureFlags_Part1> TileGateFlags = new Dictionary<int, ArmyActionFailureFlags_Part1>();
    public int TileGateCalls;
    public int UnmaskedGateCalls; // the cursor slot asks unmasked: money and influence stay in
    public string TileGateModes = ""; // every mode the tile gate was asked for: m = masked, u = unmasked
    public string ArmyGateModes = ""; // every mode the army-level gate was asked for
    public Dictionary<int, FixedPoint> TileGateMoney = new Dictionary<int, FixedPoint>();
    public Dictionary<int, FixedPoint> TileGateInfluence = new Dictionary<int, FixedPoint>();
    public Dictionary<int, FixedPoint> TileGateUnit = new Dictionary<int, FixedPoint>();

    public bool EvaluateTile(object army, int tileIndex, object districtDefinition, out DistrictPlacementEvaluation.ValidTile validTile, out FixedPoint score)
    {
        var vt = default(DistrictPlacementEvaluation.ValidTile);
        vt.Initialize(tileIndex);
        validTile = vt;
        int declared;
        score = ScoreByTile.TryGetValue(tileIndex, out declared) ? declared : tileIndex;
        return true;
    }

    public int DistrictFrame = 1;

    public bool GetCurrentFrames(out int districtFrame, out int constructionFrame, out int visibilityFrame)
    {
        districtFrame = DistrictFrame; constructionFrame = 1; visibilityFrame = 1;
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
        return (t < 0 || t > 3) ? -1 : t;
    }

    public TerritoryFactsResult GetTerritoryFacts(int territoryIndex, out TerritoryFacts facts)
    {
        facts = default(TerritoryFacts);
        facts.IsEdgeOfTheWorld = false;
        facts.Claimed = false;
        facts.HasSettlement = HasSettlement;
        facts.OwnerEmpireIndex = HasSettlement ? SettlementOwner : -1;
        facts.IsOwnedByCity = SettlementIsCity;
        facts.Tiles = new int[] { 100 + territoryIndex * 10 + 1, 100 + territoryIndex * 10 + 2, 100 + territoryIndex * 10 + 3 };
        return TerritoryFactsResult.Known;
    }

    /// <summary>Placement-neighborhood relation: a 0->1->2->3 chain, the last territory a known
    /// leaf (it reports no edges, which is a known answer, not an unknown).</summary>
    public bool TryGetAdjacentTerritories(int territoryIndex, out Territory.Adjacency[] edges)
    {
        var reported = new Territory.Adjacency[territoryIndex < 3 ? 1 : 0];
        for (int i = 0; i < reported.Length; i++)
            reported[i] = new Territory.Adjacency { TerritoryIndex = territoryIndex + 1, Transition = TerritoryTransition.Open };
        edges = reported;
        return true;
    }

    /// <summary>The mode's tile gate: scripted per-tile flags and costs, with the mask applied when
    /// the pipeline asks for it, and call counters so "no tile asked" and "the cursor slot asked
    /// unmasked" are both checkable.</summary>
    public bool TryGetTileGateFailures(ArmyActionType mode, object army, int tileIndex, bool maskMoneyAndInfluence,
        out ArmyActionFailureFlags failures, out FixedPoint moneyCost, out FixedPoint influenceCost, out FixedPoint unitCost)
    {
        TileGateCalls++;
        if (!maskMoneyAndInfluence) UnmaskedGateCalls++;
        TileGateModes += mode + (maskMoneyAndInfluence ? "m " : "u ");
        ArmyActionFailureFlags_Part1 scripted;
        failures = (ArmyActionFailureFlags)(TileGateFlags.TryGetValue(tileIndex, out scripted) ? scripted : 0);
        if (maskMoneyAndInfluence) failures = (ArmyActionFailureFlags)GameApi.ApplyMoneyInfluenceMask(failures);
        moneyCost = Cost(TileGateMoney, tileIndex);
        influenceCost = Cost(TileGateInfluence, tileIndex);
        unitCost = Cost(TileGateUnit, tileIndex);
        return army != null;
    }

    private static FixedPoint Cost(Dictionary<int, FixedPoint> byTile, int tileIndex)
    {
        FixedPoint declared;
        return byTile.TryGetValue(tileIndex, out declared) ? declared : FixedPoint.Zero;
    }

    public bool TryGetArmyGateFailures(ArmyActionType mode, object army, out ArmyActionFailureFlags failures)
    {
        ArmyGateModes += mode + " ";
        if (ThrowAtTheGate) throw new InvalidOperationException("pipeline failure at the army gate");
        failures = (ArmyActionFailureFlags)ArmyGateFlags;
        return army != null;
    }
}

/// <summary>Counts fx calls so star-render checks have evidence.</summary>
internal sealed class CountingCanvas : PickCanvas
{
    public int Started;
    public int Stopped;
    public bool Available { get { return true; } }
    public int StartFx(int tileIndex) { Started++; return tileIndex + 1; }
    public void StopFx(int fxHandle) { Stopped++; }
}

/// <summary>
/// DEBUG [noopc] write-side contract harness (SPEC section 6). All three settle types share
/// CampPlacementEvaluation, and the city no-op contract is retired: the advisor writes through the
/// active mode's pipeline into that mode's evaluation only, so the surviving write-side rows are
/// tail reservation from the config-derived capacity, the barrier-then-count order, the full-region
/// scrub on every activation, the incremental zero-fill, the slot stability, and the shown set.
/// Checks compare a full signature of the mutable state before and after, catching any mutation,
/// not just known ones. Camp placement, city founding and settler founding are the wired controls:
/// tail reserved, picks written Showable, vanilla's own glyphs untouched, stars and the pin gate
/// answering the published membership, the cursor slot from the mode gate call with no mask. A mode
/// the mode-policy table has not wired is the one that writes nothing. The pin gate is driven
/// through the real postfix, and vanilla's unpatched pin filter chain is driven directly.
/// Presentation facts are read off the published channel (PresentationView.Current) and driven
/// through the real publish path - a refresh publishes the mode plus the hovered tile's unmasked
/// tile-gate answer - never by stamping live game fields. The startup-bound name table is resolved
/// and required before any production entry point runs, because the patch targets bind through it.
/// Additional rows: the army-level gate answering the shared army-state rows (battle-locked, locked)
/// before the mode's own, mode exit taking the channel with the cursor, and a pipeline throw never
/// falling back to vanilla.
/// </summary>
internal static class Program
{
    private static int s_Failures;
    private static int s_Checks;

    // The harness surface, reachable from the publish-path helper: a published fact is driven by
    // refreshing a scripted gate answer, never by stamping a live game field.
    private static GraphSurface s_Surface;

    // Hand-computed from the derivation SPEC section 6 row 1 documents: the neighborhood territory
    // budget (the hovered territory plus 8 fan-out territories per hop beyond level 1) times the
    // per-territory worst case (the larger quota plus the 8-slot tie headroom).
    private const int DefaultTailCapacity = 90; // quotas 2/1, 2 hops: 9 x (2 + 8)
    private const int WideTailCapacity = 170;   // quotas 2/1, 3 hops: 17 x (2 + 8)
    private const int RoomyQuotaCapacity = 117; // quotas 5/3, 2 hops: 9 x (5 + 8)
    private const int SingleHopCapacity = 9;    // quotas 1/1, 1 hop: 1 x (1 + 8)
    private const int GrownPickCount = 200;     // past every reserve above: the grow-on-demand case
    private const int ReserveCeiling = 4096;    // the saturation point of the derived reserve

    private static void Check(string name, bool ok)
    {
        Console.WriteLine("[noopc] " + (ok ? "OK   " : "FAIL ") + name + (ok ? "" : "  [set: " + DescribeSet() + "]"));
        s_Checks++;
        if (!ok) s_Failures++;
    }

    // Ticket 21 (SPEC section 8). The three help strings are copied verbatim from SPEC section 8
    // and the capacities come from the hand-computed derivation above: expected values come from
    // the spec, never from the code under test.
    private const string SpecHelpTopHovered = "Admission quota on the hovered territory: this many tiles minimum, plus every tile tied with the last admitted score.";
    private const string SpecHelpTopNeighbour = "Admission quota on every other territory in the placement neighborhood: this many tiles minimum, plus every tile tied with the last admitted score.";
    private const string SpecHelpRangeLevels = "Placement-neighborhood hops from the hovered territory, the hovered territory counts as level 1: 3 = hovered + adjacent + adjacent-to-adjacent. Every adjacency edge counts as a hop: sea, cliff, mountain and closed-border edges included.";

    /// <summary>The release cfg shape: production AdvisorConfig.Bind against a fresh config file,
    /// saved the way the game and scripts/cfggen save it.</summary>
    private static string GeneratedCfg()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "noopcircuit-configsurface.cfg");
        var file = new BepInEx.Configuration.ConfigFile(path, false,
            new BepInEx.BepInPlugin(AdvisorConfig.PluginGuid, AdvisorConfig.PluginName, "0.0.0-harness"));
        file.SaveOnConfigSet = false;
        AdvisorConfig.Bind(file);
        file.Save();
        return System.IO.File.ReadAllText(path);
    }

    /// <summary>Every config key the saved file carries: the knob list of the shipped surface.</summary>
    private static List<string> CfgKeys(string cfg)
    {
        var keys = new List<string>();
        foreach (var raw in cfg.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("[")) continue;
            var eq = line.IndexOf('=');
            if (eq > 0) keys.Add(line.Substring(0, eq).Trim());
        }
        return keys;
    }

    /// <summary>The entry block a key owns in the saved file: help line, type, default and value.</summary>
    private static string CfgEntry(string cfg, string key)
    {
        var lines = cfg.Split('\n');
        var block = new StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Trim().StartsWith(key + " =")) continue;
            for (int j = System.Math.Max(0, i - 4); j <= i; j++) block.AppendLine(lines[j].Trim());
        }
        return block.ToString();
    }

    private static string DescribeSet()
    {
        var view = PresentationView.Current;
        var set = view.Picks;
        if (set == null || set.Tiles == null) return "INACTIVE under " + view.Mode;
        var sb = new StringBuilder("tiles=");
        for (int i = 0; i < set.Tiles.Length; i++) sb.Append(set.Tiles[i]).Append(' ');
        return sb.ToString();
    }

    /// <summary>
    /// An evaluation shaped like a vanilla activation: baseline valid positions with the
    /// top-2 display flag on (ReplaceBestCampPlacementAtIndex is what sets Showable), and
    /// sentinel indices in every slot above the baseline, so a write into a reserved slot is
    /// visible in the signature.
    /// </summary>
    private static CampPlacementEvaluation NewEvaluation(ArmyActionType settleType, int baselineCount)
    {
        var eval = new CampPlacementEvaluation();
        var tiles = new DistrictPlacementEvaluation.ValidTile[baselineCount + 32];
        for (int i = 0; i < baselineCount; i++)
        {
            var vt = default(DistrictPlacementEvaluation.ValidTile);
            vt.Initialize(5000 + i);
            vt.Showable = i < 2;
            tiles[i] = vt;
        }
        for (int i = baselineCount; i < tiles.Length; i++)
        {
            var vt = default(DistrictPlacementEvaluation.ValidTile);
            vt.Initialize(-1000 - i);
            tiles[i] = vt;
        }
        eval.ValidTiles = tiles;
        eval.ValidTileCount = baselineCount;
        eval.ConditionalFlagsTileIndexes = new DistrictPlacementConditionFlags[tiles.Length];
        eval.CurrentPositionIndex = 0;
        eval.SettleType = settleType;
        return eval;
    }

    private static string Signature(CampPlacementEvaluation e)
    {
        var sb = new StringBuilder();
        sb.Append("count=").Append(e.ValidTileCount)
          .Append(" pos=").Append(e.CurrentPositionIndex)
          .Append(" tilesLen=").Append(e.ValidTiles.Length)
          .Append(" flagsLen=").Append(e.ConditionalFlagsTileIndexes.Length)
          .Append(" settle=").Append(e.SettleType);
        for (int i = 0; i < e.ValidTiles.Length; i++)
            sb.Append('|').Append(e.ValidTiles[i].TileIndex).Append(e.ValidTiles[i].Showable ? 'S' : '-');
        for (int i = 0; i < e.ConditionalFlagsTileIndexes.Length; i++)
            sb.Append('|').Append((int)e.ConditionalFlagsTileIndexes[i]);
        return sb.ToString();
    }

    /// <summary>
    /// Drive the real per-refresh entry (the UpdateCurrentPosition takeover). Its return value is
    /// vanilla's own answer: false = vanilla's body, its hover append rule included, is skipped and
    /// the advisor owns the cursor slot; true = vanilla keeps running its own refresh, its own
    /// cursor entry included, and the advisor writes nothing.
    /// </summary>
    private static bool Refresh(CampPlacementEvaluation eval, int cursorTile)
    {
        var prefix = typeof(SettlementEvaluationPatch).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static);
        return (bool)prefix.Invoke(null, new object[] { eval, cursorTile, new SimulationEntityGUID(1UL) });
    }

    /// <summary>
    /// Drive the real pin-gate postfix. Expected values encode the gate contract.
    /// </summary>
    private static void Gate(string name, bool vanilla, int tileIndex, bool expected)
    {
        var postfix = typeof(SettlementPinsPatch).GetMethod("Postfix", BindingFlags.NonPublic | BindingFlags.Static);
        var info = default(DistrictPlacementEvaluation.ValidTile);
        info.Initialize(tileIndex);
        var args = new object[] { vanilla, info };
        postfix.Invoke(null, args);
        Check(name, (bool)args[0] == expected);
    }

    /// <summary>
    /// Drive the real takeover prefix (EvaluatePlacement). Its return value is the mode-dispatch
    /// decision: false skips vanilla's full settlement evaluation, true leaves vanilla in charge.
    /// </summary>
    private static bool ScanPass(SettlementPlacementCursorSnapshot snapshot, CampPlacementEvaluation eval, ArmyActionType mode, int cursorTile)
    {
        WorldPosition.SetWorldSize(10, 1000, false); // so a tile index round-trips through ToTileIndex
        SetMember(snapshot, "actionType", mode);
        SetMember(snapshot, "nextConstructiblePosition", new WorldPosition(cursorTile));
        var prefix = typeof(PlacementScanPatch).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static);
        return (bool)prefix.Invoke(null, new object[] { snapshot, new SimulationEntityGUID(1UL), eval });
    }

    /// <summary>The pin gate's answer for a tile, with vanilla's own answer as the input.</summary>
    private static bool GateAnswer(int tileIndex, bool vanilla = true)
    {
        var postfix = typeof(SettlementPinsPatch).GetMethod("Postfix", BindingFlags.NonPublic | BindingFlags.Static);
        var info = default(DistrictPlacementEvaluation.ValidTile);
        info.Initialize(tileIndex);
        var args = new object[] { vanilla, info };
        postfix.Invoke(null, args);
        return (bool)args[0];
    }

    private static void SetMember(object target, string field, object value)
    {
        for (var type = target.GetType(); type != null; type = type.BaseType)
        {
            var f = type.GetField(field, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (f != null) { f.SetValue(target, value); return; }
        }
        throw new Exception("snapshot member not found: " + field);
    }

    private static bool Inactive()
    {
        return !PresentationView.Current.IsActive;
    }

    /// <summary>The published pick content: the channel's tiles, empty while the channel is
    /// inactive. Published facts come off the channel - the only source the pin gate and the star
    /// renderer read - never off a harness-side copy of the pick set.</summary>
    private static int[] PublishedTiles()
    {
        var picks = PresentationView.Current.Picks;
        return picks == null || picks.Tiles == null ? new int[0] : picks.Tiles;
    }

    private static bool AscendingUnique(int[] tiles)
    {
        if (tiles == null || tiles.Length == 0) return false;
        for (int i = 1; i < tiles.Length; i++)
            if (tiles[i] <= tiles[i - 1]) return false;
        return true;
    }

    /// <summary>
    /// How many of the published pick tiles appear at the advisor's PICK slots, i.e. from `from`
    /// (the writer's cursor slot + 1) to the end of the array. The cursor slot is excluded: on the
    /// refresh it holds the hovered tile's own entry, which is a member whenever the cursor hovers
    /// a pick - counting it would double one tile and make the pick-slot count unreadable.
    /// </summary>
    private static int ScanTailHits(CampPlacementEvaluation e, int[] tiles, int from)
    {
        var hits = 0;
        for (int i = System.Math.Max(from, 0); i < e.ValidTiles.Length; i++)
            for (int t = 0; t < tiles.Length; t++)
                if (e.ValidTiles[i].TileIndex == tiles[t]) hits++;
        return hits;
    }

    /// <summary>The vanilla best-placement arrays a retired cascade leaves unpopulated.</summary>
    private static bool AllMinusOne(int[] indexes)
    {
        if (indexes == null || indexes.Length == 0) return false;
        for (int i = 0; i < indexes.Length; i++)
            if (indexes[i] != -1) return false;
        return true;
    }

    /// <summary>Every advisor pick slot a published member occupies: the FIMS yields vanilla's
    /// zero-gains-and-zero-losses pin filter tests (PlacementPinsSubset.cs:52-55). The synthetic
    /// surface evaluates tiles through ValidTile.Initialize, which is a zero-yield tile, so every
    /// member the advisor admits here is one vanilla's filter rejects a pin for. The cursor slot is
    /// excluded: its tile is the hovered tile, a member whenever the cursor sits on a pick.</summary>
    private static bool AllMemberEntriesZeroYield(CampPlacementEvaluation e, int[] members, int from)
    {
        var seen = 0;
        for (int i = System.Math.Max(from, 0); i < e.ValidTileCount; i++)
        {
            var entry = e.ValidTiles[i];
            if (!ContainsTile(members, entry.TileIndex)) continue;
            if (entry.OverallGains.CountFimsType() != 0 || entry.OverallLosses.CountFimsType() != 0) return false;
            seen++;
        }
        return seen == members.Length;
    }

    /// <summary>A writer-scrubbed slot: the Initialize(-1) shape vanilla skips.</summary>
    private static DistrictPlacementEvaluation.ValidTile ScrubbedEntry()
    {
        var cleared = default(DistrictPlacementEvaluation.ValidTile);
        cleared.Initialize(-1);
        return cleared;
    }

    /// <summary>
    /// Every advisor-owned slot above the live count, up to `windowEnd`, is the zeroed shape: no
    /// tile, no FIMS, no glyph. SPEC section 6 row 3, the advisor's duty from the reservation on.
    /// </summary>
    private static bool ZeroFilledAboveCount(CampPlacementEvaluation e, int count, int windowEnd)
    {
        for (int i = count; i < System.Math.Min(e.ValidTiles.Length, windowEnd); i++)
        {
            var entry = e.ValidTiles[i];
            if (entry.TileIndex != -1) return false;
            if (entry.OverallGains.CountFimsType() != 0 || entry.OverallLosses.CountFimsType() != 0) return false;
            if (entry.Showable) return false;
        }
        return true;
    }

    /// <summary>
    /// Drive vanilla's sealed generic pin filter chain, the code the mod never patches: it runs
    /// its generic filters and only then calls the hook the pin gate postfix overrides. The tile
    /// filter is the headless-reachable one; the exploration filter behind it needs the
    /// presentation visibility controller, which exists only in a running game.
    /// </summary>
    private static bool VanillaPinFilter(DistrictPlacementEvaluation.ValidTile info)
    {
        var subset = Activator.CreateInstance(typeof(Amplitude.Mercury.UI.SettlementPlacementPinsSubset));
        var filter = typeof(Amplitude.Mercury.UI.PlacementPinsSubset)
            .GetMethod("PinMustBeShown", BindingFlags.NonPublic | BindingFlags.Instance);
        return (bool)filter.Invoke(subset, new object[] { info, 0 });
    }

    /// <summary>
    /// The published mode and the published cursor-slot failure, driven the way production produces
    /// them: a refresh in the mode under test asks the mode's tile gate with no mask for the hovered
    /// tile, writes the cursor slot, and publishes the mode, the admission set and that gate answer
    /// on the presentation channel. The rows that used to stamp SettleType/SettleFailure on the
    /// snapshot's own evaluation drive this instead: the pin gate reads the published facts, never
    /// the live evaluation's.
    /// </summary>
    private static void PublishHovered(CampPlacementEvaluation eval, int hoveredTile)
    {
        s_Surface.DistrictFrame++; // cache-key bump: the published facts must come from this refresh
        Refresh(eval, hoveredTile);
    }

    /// <summary>Membership answered independently of the mod: a plain scan of the published tiles.</summary>
    private static bool ContainsTile(int[] tiles, int tileIndex)
    {
        for (int i = 0; i < tiles.Length; i++)
            if (tiles[i] == tileIndex) return true;
        return false;
    }

    /// <summary>
    /// The shown-set invariant over the advisor's own slot window: every published member
    /// Showable, every advisor-owned slot outside the set not Showable, and exactly the member
    /// count shown. Vanilla's slots are outside the window: the advisor owns none of them.
    /// </summary>
    private static bool ShowableIsMembership(CampPlacementEvaluation e, int baseline, int[] members)
    {
        var shown = 0;
        for (int i = baseline + 1; i < e.ValidTileCount; i++)
        {
            var member = ContainsTile(members, e.ValidTiles[i].TileIndex);
            if (e.ValidTiles[i].Showable != member) return false;
            if (member) shown++;
        }
        return shown == members.Length;
    }

    /// <summary>
    /// The advisor's own slots inside the live count that carry the scrubbed shape (no tile,
    /// not shown): the slots this refresh released. A live entry that is neither a member nor
    /// scrubbed breaks the invariant and reports -1.
    /// </summary>
    private static int ScrubbedAdvisorSlots(CampPlacementEvaluation e, int baseline, int[] members)
    {
        var scrubbed = 0;
        for (int i = baseline + 1; i < e.ValidTileCount; i++)
        {
            var entry = e.ValidTiles[i];
            if (entry.TileIndex == -1 && !entry.Showable) scrubbed++;
            else if (!ContainsTile(members, entry.TileIndex)) return -1;
        }
        return scrubbed;
    }

    /// <summary>Vanilla's own glyph flags, slot by slot: the writer owns none of them.</summary>
    private static string VanillaGlyphs(CampPlacementEvaluation e, int baseline)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < baseline; i++)
            sb.Append('|').Append(e.ValidTiles[i].TileIndex).Append(e.ValidTiles[i].Showable ? 'S' : '-');
        return sb.ToString();
    }

    /// <summary>Every advisor-owned slot above the activation baseline is the scrubbed shape: the
    /// tail a previous mode's session left is released on an activation, in every mode (SPEC
    /// section 6 row 4), so no settlement entry survives into a city session and none survives a
    /// city session into a settlement one.</summary>
    private static bool ReleasedTail(CampPlacementEvaluation e, int baseline)
    {
        for (int i = baseline + 1; i < e.ValidTiles.Length; i++)
            if (e.ValidTiles[i].TileIndex != -1 || e.ValidTiles[i].Showable) return false;
        return true;
    }

    /// <summary>How many gate calls the mode log recorded for one label.</summary>
    private static int ModeCalls(string gateLog, string label)
    {
        var calls = 0;
        for (int i = 0; i < gateLog.Length; i++)
            if (gateLog.IndexOf(label, i) == i) calls++;
        return calls;
    }

    private static int Main()
    {
        var surface = new GraphSurface();
        s_Surface = surface;
        var plugin = new Plugin();
        plugin.Writer = new EvaluationWriter();
        var canvas = new CountingCanvas();
        plugin.Stars = new StarRenderer(canvas);
        plugin.Advisor = new Advisor(surface, new PickComputation(surface), plugin.Writer, plugin.Stars);
        Plugin.Instance = plugin;
        const int baseline = 6;

        // F2 (startup coverage, SPEC section 8 + section 1): every Harmony patch target the patch
        // surface binds comes from the startup-bound name table, so the harness resolves that table
        // exactly as the composition root does - and requires the resolved surface - before it drives
        // a single production entry point.
        // The startup probe binds by scanning the loaded assemblies; in the game every game assembly
        // is loaded long before a plugin's Awake, so the probe sees the whole surface. The harness's
        // own entry point touches only the simulation-side types, so the two presentation-side
        // assemblies the patch surface binds are touched here first: the mod patches
        // SettlementPlacementCursor.DeactivateCursor (presentation) and
        // SettlementPlacementPinsSubset.PinMustMeShown (UI), and an assembly the probe has not loaded
        // is a name it cannot resolve.
        var patchHosts = new System.Reflection.Assembly[]
        {
            typeof(Amplitude.Mercury.Presentation.SettlementPlacementCursor).Assembly,
            typeof(Amplitude.Mercury.UI.SettlementPlacementPinsSubset).Assembly,
        };
        var loadedAssemblies = System.AppDomain.CurrentDomain.GetAssemblies();
        var patchHostsLoaded = true;
        for (int i = 0; i < patchHosts.Length; i++)
            patchHostsLoaded &= System.Array.IndexOf(loadedAssemblies, patchHosts[i]) >= 0;
        Check("the assemblies the patch surface binds are loaded when the startup probe scans them",
            patchHostsLoaded);
        GameApi.Resolve();
        if (!GameApi.ReadyStatic) Console.WriteLine("[noopc] resolve failed: " + GameApi.ResolveError);
        Check("the startup probe resolves every required game member before the harness drives production",
            GameApi.ReadyStatic);
        var patchTargets = string.Join(" | ", GameApi.RequiredNames);
        Check("every patch target the patch surface binds is a startup-checked name",
            patchTargets.Contains("Patch.SettlementPlacementCursorSnapshot.EvaluatePlacement")
            && patchTargets.Contains("Patch.CampPlacementEvaluation.UpdateCurrentPosition")
            && patchTargets.Contains("CampPlacementEvaluation.Initialize(CustomTileIndexArea)")
            && patchTargets.Contains("Patch.SettlementPlacementCursor.DeactivateCursor")
            && patchTargets.Contains("Patch.SettlementPlacementPinsSubset.PinMustMeShown"));

        // A. A mode the mode-policy table has not wired stays vanilla's in every entry point: it
        // writes nothing and publishes nothing. The city no-op contract is retired (ticket 17): the
        // advisor writes through the active mode's pipeline only, so an unwired mode is the one that
        // writes nothing. All three settlement modes (camp, FoundCity, FoundCityWithSettler) are now
        // wired rows, each driven as a wired control below (blocks B, D2 and D2b).

        var unwired = NewEvaluation(ArmyActionType.Ransack, baseline);
        var unwiredClean = Signature(unwired);
        plugin.Writer.NoteActivation(unwired);
        Refresh(unwired, 5002);
        Check("an unmapped mode: no tail claim, zero writes, pick set inactive",
            Signature(unwired) == unwiredClean && Inactive());

        // B. Camp control: the shown set. Stars and glyphs are the published membership; the
        // writer owns the tail slots only, and vanilla's own glyph flags are its to keep.
        var settlement = NewEvaluation(ArmyActionType.CreateCamp, baseline);
        var settlementGlyphs = VanillaGlyphs(settlement, baseline);
        plugin.Writer.NoteActivation(settlement);
        Check("CreateCamp activation reserves the tail window",
            settlement.ValidTiles.Length == baseline + 1 + DefaultTailCapacity);
        Check("the cursor slot is the activation baseline",
            plugin.Writer.CursorSlotIndex(settlement) == baseline);

        Refresh(settlement, 131); // cursor tile inside the synthetic territory space (territory 3)
        var tiles = PublishedTiles();
        int inTail = 0;
        for (int i = baseline + 1; i < settlement.ValidTileCount; i++)
            for (int t = 0; t < tiles.Length; t++)
                if (settlement.ValidTiles[i].TileIndex == tiles[t]) inTail++;

        Check("CreateCamp refresh publishes ascending unique tiles", AscendingUnique(tiles));
        Check("CreateCamp refresh writes every pick into its tail slot",
            settlement.ValidTileCount > baseline + 1 && inTail == tiles.Length);
        Check("CreateCamp refresh: Showable equals pick-set membership in the advisor's slots",
            ShowableIsMembership(settlement, baseline, tiles));
        Check("CreateCamp refresh: vanilla's glyph flags are untouched",
            VanillaGlyphs(settlement, baseline) == settlementGlyphs);
        plugin.Stars.Render(PresentationView.Current.Picks);
        Check("settlement pick set starts one star per pick", canvas.Started == tiles.Length);

        // The tail slots the cursor hovers outside any pick: the refresh writes the cursor slot
        // itself and points the cursor index at it, and the slots this refresh releases zero-fill
        // above the live count (SPEC section 6 row 3).
        Check("every advisor slot above the live count is zeroed",
            ZeroFilledAboveCount(settlement, settlement.ValidTileCount, settlement.ValidTiles.Length));

        // A smaller admission set releases slots. The writer zero-fills what it released, so
        // inside the live count the published membership stays the only live entry set.
        surface.TileGateFlags[132] = ArmyActionFailureFlags_Part1.TileIsOccupied;
        surface.TileGateFlags[133] = ArmyActionFailureFlags_Part1.TileIsOccupied;
        surface.DistrictFrame++;
        Refresh(settlement, 131);
        var shrunk = PublishedTiles();
        plugin.Stars.Render(PresentationView.Current.Picks);
        Check("a smaller pick set scrubs the slots it released",
            shrunk.Length < tiles.Length
            && ShowableIsMembership(settlement, baseline, shrunk)
            && ScrubbedAdvisorSlots(settlement, baseline, shrunk) >= tiles.Length - shrunk.Length);
        surface.TileGateFlags.Clear();
        surface.DistrictFrame++;
        Refresh(settlement, 131);
        Check("the restored pick set re-occupies its invariant slots",
            ShowableIsMembership(settlement, baseline, PublishedTiles()));
        plugin.Stars.Render(PresentationView.Current.Picks);

        // C. Pin gate through the real postfix: published-set membership plus vanilla's hovered rule,
        // every fact answered from the published channel - the mode the last refresh published, the
        // active pick set, and the cursor-slot failure that refresh's unmasked gate call produced for
        // the hovered tile (SPEC section 5 rows 2 and 5). The old rows stamped SettleType and
        // SettleFailure on the snapshot's own evaluation; the fix moved both facts onto the channel.
        var snapshot = CreateSnapshot();
        int bestTile = 5001; // a vanilla best-2 tile: never an advisor pick (picks are 13x tiles)
        snapshot.PresentationData.PlacementEvaluation.BestCampPlacementTileIndexes = new[] { bestTile, -1 };
        Snapshots.SettlementPlacementCursorSnapshot = snapshot;

        // The channel as the last camp refresh left it: mode CreateCamp, the restored admission set,
        // and no cursor-slot failure - the hovered tile's unmasked gate call answered clean.
        int pickTile = PublishedTiles().Length > 0 ? PublishedTiles()[0] : -1;
        Gate("camp: a member shows its pin", true, pickTile, true);
        Gate("camp: a member shows its pin with vanilla answering off", false, pickTile, true);
        Gate("camp: the vanilla best tile outside the set shows no pin", true, bestTile, false);
        surface.TileGateFlags[131] = ArmyActionFailureFlags_Part1.NotEnoughInfluence;
        PublishHovered(settlement, 131); // publishes the mode with the hovered tile's unmasked answer
        Gate("camp: an influence-only cursor failure keeps the member pin", true, pickTile, true);
        surface.TileGateFlags[131] = ArmyActionFailureFlags_Part1.NotEnoughMoney;
        PublishHovered(settlement, 131);
        Gate("camp: a money cursor failure drops the member pin", true, pickTile, false);
        surface.TileGateFlags[131] = ArmyActionFailureFlags_Part1.ArmyLocked;
        PublishHovered(settlement, 131);
        Gate("camp: any other cursor failure drops the member pin", true, pickTile, false);
        surface.TileGateFlags.Clear();
        PublishHovered(settlement, 131);

        canvas.Started = 0;
        canvas.Stopped = 0;
        plugin.Stars.Render(PresentationView.Current.Picks);
        Check("settlement pick set re-render is version-gated (no fx churn)", canvas.Started == 0);
        plugin.Advisor.Deactivate(ArmyActionType.CreateCamp);
        plugin.Stars.Render(PresentationView.Current.Picks);
        Check("deactivation stops every tracked star", canvas.Stopped == tiles.Length && canvas.Started == 0);
        Check("Deactivate publishes the Tiles==null inactive representation",
            PresentationView.Current.Picks != null && PresentationView.Current.Picks.Tiles == null);
        Gate("camp INACTIVE: the vanilla best tile shows no pin", true, bestTile, false);
        Gate("camp INACTIVE: a former pick tile shows no pin", true, pickTile, false);

        int deactivatedVersion = PresentationView.Current.Version;
        plugin.Advisor.Deactivate(ArmyActionType.CreateCamp);
        Check("Deactivate is idempotent (no version churn per refresh)",
            PresentationView.Current.Version == deactivatedVersion && PresentationView.Current.Picks.Tiles == null);

        // The fix changed this row's shape: the channel is never null - its pre-publish boot state is
        // INACTIVE under a mode the table has not wired - so "no set published" is driven as the
        // publish it replaces: INACTIVE under a wired mode, which shows no pin at all.
        PresentationView.PublishInactive(ArmyActionType.CreateCamp);
        Gate("camp with nothing published: no pin shows", true, bestTile, false);
        Gate("camp with nothing published: a former member shows no pin", true, pickTile, false);
        PresentationView.PublishInactive(ArmyActionType.Ransack); // the boot shape: INACTIVE, unwired mode
        Gate("a mode the table has not wired keeps a vanilla pin answer on", true, bestTile, true);
        Gate("a mode the table has not wired keeps a vanilla pin answer off", false, pickTile, false);
        // The mode the gate answers for follows the channel: the camp refresh below republishes it.

        // C2. Zero-pick publish (every neighborhood territory claimed/all-water): the advisor
        // publishes INACTIVE and displays nothing: stars cleared, no pick entries, and no
        // vanilla surface taking the advisor's place.
        surface.AllWater = true;
        surface.DistrictFrame++; // cache-key bump: per-territory caches must miss
        Refresh(settlement, 130);
        Check("zero-pick refresh deactivates the pick set (no active empty set)", Inactive());
        int foreignSlots = 0;
        for (int i = 0; i < settlement.ValidTileCount; i++)
        {
            int idx = settlement.ValidTiles[i].TileIndex;
            if (i < baseline + 1) continue; // vanilla entries are never touched
            if (idx == -1) continue; // scrubbed advisor slot: vanilla skips it
            if (idx == 130) continue; // vanilla's own cursor entry
            foreignSlots++;
        }
        Check("zero-pick refresh leaves no pick entries inside the valid window",
            Inactive() && foreignSlots == 0);
        Gate("zero-pick refresh: the vanilla best tile shows no pin", true, bestTile, false);
        Gate("zero-pick refresh: the cursor tile shows no pin", true, 130, false);

        // C3. Sea-flagged territory hosting land camp sites (observed in-game: all 25 valid
        // camp sites sat in a territory the AI snapshot flags IsOcean): the advisor must
        // publish its picks from it; water is filtered per tile, never by the territory flag.
        surface.AllWater = false; surface.AllOcean = true; // land tiles inside a sea-flagged territory
        surface.DistrictFrame++;
        Refresh(settlement, 133);
        Check("sea-flagged territory with land tiles publishes picks",
            !Inactive() && AscendingUnique(PublishedTiles()));
        Gate("sea-flagged territory: pick pins are forced", false, 132, true);
        surface.AllOcean = false;
        surface.DistrictFrame++;
        Refresh(settlement, 134); // restore: land picks on the same tiles
        Check("flag off restores the same picks",
            !Inactive());

        // D. The !Ready half of the Run gate: a not-ready refresh writes nothing in a wired mode
        // and deactivates.
        surface.ReadyFlag = false;
        var settlementBeforeNotReady = Signature(settlement);
        Refresh(settlement, 131);
        Check("not-ready settlement refresh: zero writes, pick set inactive",
            Signature(settlement) == settlementBeforeNotReady && Inactive());
        surface.ReadyFlag = true;

        // D2. City founding is a wired row of the mode-policy table (ticket 17). The city session
        // publishes an admission set under the same admission and presentation contract as camp -
        // quota plus top tie set, membership pin gate, INACTIVE displays nothing - writes only into
        // its own evaluation, and reports the founding cost on the cursor slot.
        var city = NewEvaluation(ArmyActionType.FoundCity, baseline);
        var cityGlyphs = VanillaGlyphs(city, baseline);
        var campUntouched = Signature(settlement);
        plugin.Writer.NoteActivation(city);
        Check("FoundCity activation reserves the tail window on its own evaluation",
            city.ValidTiles.Length == baseline + 1 + DefaultTailCapacity
            && plugin.Writer.CursorSlotIndex(city) == baseline);
        Check("a city activation claims no tail on a foreign evaluation",
            Signature(settlement) == campUntouched);

        int citySlot = plugin.Writer.CursorSlotIndex(city);
        snapshot.PresentationData.PlacementEvaluation = city;
        // The published mode and the published cursor-slot failure come from the refresh below: the
        // city refresh publishes FoundCity together with the hovered tile's unmasked gate answer (no
        // flag scripted on it, so no failure). The pin gate reads those published facts, never this
        // evaluation's own fields.
        surface.ScoreByTile[131] = 200;
        surface.TileGateCalls = 0;
        surface.TileGateModes = "";
        surface.ArmyGateModes = "";
        canvas.Started = 0;
        canvas.Stopped = 0;
        Check("the advisor owns the city refresh: vanilla's whole hover body is skipped",
            !Refresh(city, 131));
        var cityTiles = PublishedTiles();
        Check("FoundCity refresh publishes ascending unique tiles", AscendingUnique(cityTiles));
        Check("FoundCity refresh writes every pick into its own tail slot",
            city.ValidTileCount > baseline + 1
            && ScanTailHits(city, cityTiles, baseline + 1) == cityTiles.Length);
        Check("FoundCity refresh: Showable equals pick-set membership in the advisor's slots",
            ShowableIsMembership(city, baseline, cityTiles));
        Check("FoundCity refresh: vanilla's glyph flags are untouched",
            VanillaGlyphs(city, baseline) == cityGlyphs);
        Check("the city session asks the FoundCity gates only, the cursor slot unmasked",
            surface.ArmyGateModes == "FoundCity "
            && ModeCalls(surface.TileGateModes, "FoundCitym") + ModeCalls(surface.TileGateModes, "FoundCityu") == surface.TileGateCalls
            && ModeCalls(surface.TileGateModes, "FoundCitym") > 0
            && ModeCalls(surface.TileGateModes, "FoundCityu") == 1);
        Check("the city cursor index points at the advisor's own slot, on the hovered tile",
            city.CurrentPositionIndex == citySlot && city.ValidTiles[citySlot].TileIndex == 131
            && !city.ValidTiles[citySlot].Showable);
        Check("the city writes touch no other evaluation", Signature(settlement) == campUntouched);
        plugin.Stars.Render(PresentationView.Current.Picks);
        Check("the city pick set starts one star per pick", canvas.Started == cityTiles.Length);
        Check("city pins follow the published membership",
            cityTiles.Length > 1 && GateAnswer(cityTiles[0]) && GateAnswer(cityTiles[1]) && !GateAnswer(bestTile));
        Check("every city advisor slot above the live count is zeroed",
            ZeroFilledAboveCount(city, city.ValidTileCount, city.ValidTiles.Length));

        // The unaffordable city pick (SPEC section 5 row 7): the same star and the same pin an
        // affordable pick has, and the founding failure reaching the player on hover through the
        // cursor slot - the red ghost's source in a city mode.
        surface.TileGateFlags[131] = ArmyActionFailureFlags_Part1.NotEnoughMoney
            | ArmyActionFailureFlags_Part1.NotEnoughInfluence;
        surface.TileGateMoney[131] = (FixedPoint)40;
        surface.TileGateInfluence[131] = (FixedPoint)25;
        surface.DistrictFrame++;
        Refresh(city, 130); // the cursor sits off the pick: nothing is on the hover yet
        var unaffordable = PublishedTiles();
        Check("an unaffordable city tile stays in the published set", ContainsTile(unaffordable, 131));
        plugin.Stars.Render(PresentationView.Current.Picks);
        Check("the unaffordable city pick keeps its star: no fx stopped, the same member set",
            canvas.Stopped == 0 && unaffordable.Length == cityTiles.Length);
        Check("the unaffordable city pick shows its pin", GateAnswer(131));
        int cityStars = unaffordable.Length;

        int cityUnmaskedBefore = surface.UnmaskedGateCalls;
        Refresh(city, 131); // the cursor hovers the pick: the failure surfaces on the cursor slot
        Check("the city cursor slot comes from the unmasked FoundCity gate call",
            surface.UnmaskedGateCalls == cityUnmaskedBefore + 1
            && surface.TileGateModes.EndsWith("FoundCityu "));
        Check("hovering the unaffordable pick reports the founding failure on the city cursor slot",
            (city.SettleFailure & (ArmyActionFailureFlags)ArmyActionFailureFlags_Part1.NotEnoughMoney) != 0
            && (city.SettleFailure & (ArmyActionFailureFlags)ArmyActionFailureFlags_Part1.NotEnoughInfluence) != 0
            && city.CurrentPositionIndex == citySlot
            && city.ValidTiles[citySlot].TileIndex == 131);
        Check("the hovered unaffordable pick keeps its star and its membership",
            PublishedTiles().Length == cityStars
            && ShowableIsMembership(city, baseline, PublishedTiles()));
        Check("the hovered unaffordable pick loses its pin on vanilla's hovered rule", !GateAnswer(131));

        surface.TileGateFlags.Clear();
        surface.TileGateMoney.Clear();
        surface.TileGateInfluence.Clear();
        surface.DistrictFrame++;
        Refresh(city, 131);
        Check("the clean city hover reports no founding failure and the pin is back",
            city.SettleFailure == ArmyActionFailureFlags.None
            && city.InstantMoneyCost == FixedPoint.Zero
            && city.InstantInfluenceCost == FixedPoint.Zero
            && city.InstantUnitCost == FixedPoint.Zero
            && GateAnswer(131));

        // INACTIVE in a city mode displays nothing: stars cleared, no pin, no pick entries, and no
        // vanilla surface taking the advice's place.
        canvas.Started = 0;
        canvas.Stopped = 0;
        plugin.Stars.Render(PresentationView.Current.Picks);
        surface.AllWater = true;
        surface.DistrictFrame++;
        Refresh(city, 131);
        Check("city zero picks publish INACTIVE", Inactive());
        plugin.Stars.Render(PresentationView.Current.Picks);
        Check("city INACTIVE clears every tracked star",
            canvas.Stopped == cityStars && canvas.Started == 0);
        Check("city INACTIVE shows no pin and leaves no pick entry",
            !GateAnswer(131, true) && !GateAnswer(bestTile, true)
            && ScrubbedAdvisorSlots(city, baseline, new int[0]) >= 0);
        surface.AllWater = false;
        surface.DistrictFrame++;

        surface.ReadyFlag = false;
        var cityBeforeNotReady = Signature(city);
        Refresh(city, 131);
        Check("not-ready city refresh: zero writes, pick set inactive",
            Signature(city) == cityBeforeNotReady && Inactive());
        surface.ReadyFlag = true;

        // E. Mode switch on the shared evaluation: an activation inherits no previous mode's
        // settlement state, in either direction. Vanilla Initialize only lowers ValidTileCount, so
        // the grown array and the written tail slots of the settlement session are exactly what the
        // city session then scores: every mode's activation releases them, scrubbed, never merely
        // counted down. The advisor's own slots (>= baseline) are checked separately from vanilla's
        // ([0, baseline)), which must stay untouched.
        settlement.SettleType = ArmyActionType.FoundCity;
        settlement.ValidTileCount = baseline; // what vanilla's Initialize does on the city activation
        var vanillaSlots = VanillaSlots(settlement, baseline);
        plugin.Writer.NoteActivation(settlement);
        Check("city activation on the shared array releases the settlement tail",
            ReleasedTail(settlement, baseline));
        Check("city activation on the shared array leaves vanilla's slots alone",
            VanillaSlots(settlement, baseline) == vanillaSlots);
        Check("a mode switch leaks no suppressed glyph: vanilla's flags survive the settlement session",
            VanillaGlyphs(settlement, baseline) == settlementGlyphs);
        Refresh(settlement, 131); // the city session runs its own pipeline on the released evaluation
        Check("the city session on the shared evaluation publishes and writes its own admission set",
            !Inactive() && AscendingUnique(PublishedTiles())
            && ScanTailHits(settlement, PublishedTiles(), baseline + 1) == PublishedTiles().Length
            && ShowableIsMembership(settlement, baseline, PublishedTiles()));
        settlement.SettleType = ArmyActionType.CreateCamp;
        settlement.ValidTileCount = baseline; // and back: a camp activation on the same evaluation
        plugin.Writer.NoteActivation(settlement);
        Check("camp activation releases the city tail on the shared array",
            ReleasedTail(settlement, baseline));
        settlement.SettleType = ArmyActionType.FoundCityWithSettler;
        settlement.ValidTileCount = baseline; // the settler activation on the same shared evaluation
        plugin.Writer.NoteActivation(settlement);
        Check("settler activation releases the camp tail on the shared array",
            ReleasedTail(settlement, baseline));
        settlement.SettleType = ArmyActionType.CreateCamp;
        settlement.ValidTileCount = baseline; // and back to camp: the third leg of the switch
        plugin.Writer.NoteActivation(settlement);
        Check("camp activation releases the settler tail on the shared array",
            ReleasedTail(settlement, baseline));

        // D2b. Settler city founding (ticket 18) is a wired row of the mode-policy table. The
        // settler session publishes an admission set under the same admission and presentation
        // contract as camp and city - quota plus top tie set, membership pin gate, INACTIVE
        // displays nothing - writes only into its own evaluation, asks the settler gates
        // (FillSettlerAbilityFailures), and reports the founding cost on the cursor slot from the
        // unmasked gate call.
        var settler = NewEvaluation(ArmyActionType.FoundCityWithSettler, baseline);
        var settlerGlyphs = VanillaGlyphs(settler, baseline);
        var campUntouched2 = Signature(settlement);
        plugin.Writer.NoteActivation(settler);
        Check("Settler activation reserves the tail window on its own evaluation",
            settler.ValidTiles.Length == baseline + 1 + DefaultTailCapacity
            && plugin.Writer.CursorSlotIndex(settler) == baseline);
        Check("a settler activation claims no tail on a foreign evaluation",
            Signature(settlement) == campUntouched2);

        int settlerSlot = plugin.Writer.CursorSlotIndex(settler);
        snapshot.PresentationData.PlacementEvaluation = settler;
        // As in the city block: the settler refresh below publishes the mode with the hovered tile's
        // unmasked gate answer, which is the fact the pin gate's hovered rule reads.
        surface.HasSettlement = false; surface.SettlementOwner = -1; surface.SettlementIsCity = false;
        surface.ScoreByTile[131] = 200;
        surface.TileGateCalls = 0;
        surface.TileGateModes = "";
        surface.ArmyGateModes = "";
        canvas.Started = 0;
        canvas.Stopped = 0;
        Check("the advisor owns the settler refresh: vanilla's whole hover body is skipped",
            !Refresh(settler, 131));
        var settlerTiles = PublishedTiles();
        Check("Settler refresh publishes ascending unique tiles", AscendingUnique(settlerTiles));
        Check("Settler refresh writes every pick into its own tail slot",
            settler.ValidTileCount > baseline + 1
            && ScanTailHits(settler, settlerTiles, baseline + 1) == settlerTiles.Length);
        Check("Settler refresh: Showable equals pick-set membership in the advisor's slots",
            ShowableIsMembership(settler, baseline, settlerTiles));
        Check("Settler refresh: vanilla's glyph flags are untouched",
            VanillaGlyphs(settler, baseline) == settlerGlyphs);
        Check("the settler session asks the settler gates only, the cursor slot unmasked",
            surface.ArmyGateModes == "FoundCityWithSettler "
            && ModeCalls(surface.TileGateModes, "FoundCityWithSettlerm") + ModeCalls(surface.TileGateModes, "FoundCityWithSettleru") == surface.TileGateCalls
            && ModeCalls(surface.TileGateModes, "FoundCityWithSettlerm") > 0
            && ModeCalls(surface.TileGateModes, "FoundCityWithSettleru") == 1);
        Check("the settler cursor index points at the advisor's own slot, on the hovered tile",
            settler.CurrentPositionIndex == settlerSlot && settler.ValidTiles[settlerSlot].TileIndex == 131
            && !settler.ValidTiles[settlerSlot].Showable);
        Check("the settler writes touch no other evaluation", Signature(settlement) == campUntouched2);
        plugin.Stars.Render(PresentationView.Current.Picks);
        Check("the settler pick set starts one star per pick", canvas.Started == settlerTiles.Length);
        Check("settler pins follow the published membership",
            settlerTiles.Length > 1 && GateAnswer(settlerTiles[0]) && GateAnswer(settlerTiles[1]) && !GateAnswer(bestTile));
        Check("every settler advisor slot above the live count is zeroed",
            ZeroFilledAboveCount(settler, settler.ValidTileCount, settler.ValidTiles.Length));
        int settlerStars = settlerTiles.Length;

        // The settler tile gate with the same money/influence mask (SPEC section 3): an
        // unaffordable tile stays, every other failure drops the tile, and the cursor slot reports
        // the founding failure and cost from the unmasked settler gate call on hover.
        surface.TileGateFlags[131] = ArmyActionFailureFlags_Part1.NotEnoughMoney
            | ArmyActionFailureFlags_Part1.NotEnoughInfluence;
        surface.TileGateMoney[131] = (FixedPoint)40;
        surface.TileGateInfluence[131] = (FixedPoint)25;
        surface.DistrictFrame++;
        Refresh(settler, 130); // the cursor sits off the pick
        var settlerUnaffordable = PublishedTiles();
        Check("an unaffordable settler tile stays in the published set", ContainsTile(settlerUnaffordable, 131));
        Check("the unaffordable settler pick keeps its star: no fx stopped, the same member set",
            canvas.Stopped == 0 && settlerUnaffordable.Length == settlerStars);
        Check("the unaffordable settler pick shows its pin", GateAnswer(131));
        int settlerUnmaskedBefore = surface.UnmaskedGateCalls;
        Refresh(settler, 131); // the cursor hovers the pick: the failure surfaces on the cursor slot
        Check("the settler cursor slot comes from the unmasked settler gate call",
            surface.UnmaskedGateCalls == settlerUnmaskedBefore + 1
            && surface.TileGateModes.EndsWith("FoundCityWithSettleru "));
        Check("hovering the unaffordable settler pick reports the founding failure on the cursor slot",
            (settler.SettleFailure & (ArmyActionFailureFlags)ArmyActionFailureFlags_Part1.NotEnoughMoney) != 0
            && (settler.SettleFailure & (ArmyActionFailureFlags)ArmyActionFailureFlags_Part1.NotEnoughInfluence) != 0
            && settler.CurrentPositionIndex == settlerSlot
            && settler.ValidTiles[settlerSlot].TileIndex == 131);
        surface.TileGateFlags.Clear();
        surface.TileGateMoney.Clear();
        surface.TileGateInfluence.Clear();
        surface.DistrictFrame++;
        Refresh(settler, 131);
        Check("the clean settler hover reports no founding failure and the pin is back",
            settler.SettleFailure == ArmyActionFailureFlags.None
            && settler.InstantMoneyCost == FixedPoint.Zero
            && GateAnswer(131));

        // The settler territory filter (740-753, replaceability 763-771): the empire's own
        // camp/pre-camp settlement is admitted as replaceable; every other settlement site -
        // another empire's, the empire's own city, and an unowned one - stays out, publishing
        // INACTIVE. A settlement-free territory is admitted.
        surface.HasSettlement = true; surface.SettlementOwner = 0; surface.SettlementIsCity = false;
        surface.DistrictFrame++;
        Refresh(settler, 131);
        Check("the empire's own camp/pre-camp settlement is admitted (replaceable)",
            !Inactive() && AscendingUnique(PublishedTiles()));
        surface.SettlementOwner = 2; // another empire's settlement site
        surface.DistrictFrame++;
        Refresh(settler, 131);
        Check("another empire's settlement site stays out: INACTIVE", Inactive());
        surface.SettlementOwner = 0; surface.SettlementIsCity = true; // the empire's own city site
        surface.DistrictFrame++;
        Refresh(settler, 131);
        Check("an own city settlement site stays out: INACTIVE", Inactive());
        surface.HasSettlement = false; surface.SettlementOwner = -1; surface.SettlementIsCity = false;
        surface.DistrictFrame++;
        Refresh(settler, 131);
        Check("a settlement-free territory is admitted again", !Inactive() && AscendingUnique(PublishedTiles()));

        // An army with no settler unit (FillSettlerAbilityFailures sets DontHaveAbility) hits the
        // army gate: INACTIVE publishes nothing - no stars, no pins, no cursor-slot entry.
        canvas.Started = 0; canvas.Stopped = 0;
        plugin.Stars.Render(PresentationView.Current.Picks); // reconcile the tracked fx to the active set
        int trackedSettlerStars = PublishedTiles().Length;
        surface.ArmyGateFlags = ArmyActionFailureFlags_Part1.DontHaveAbility;
        surface.TileGateCalls = 0;
        Refresh(settler, 131);
        Check("no settler unit publishes INACTIVE", Inactive());
        canvas.Started = 0; canvas.Stopped = 0; // count only the INACTIVE reconcile
        plugin.Stars.Render(PresentationView.Current.Picks);
        Check("no settler unit clears every tracked star", canvas.Stopped == trackedSettlerStars && canvas.Started == 0);
        Check("no settler unit shows no pin and takes no vanilla fallback",
            settlerTiles.Length > 0 && !GateAnswer(settlerTiles[0], true) && !GateAnswer(bestTile, true));
        Check("no settler unit asks no tile gate", surface.TileGateCalls == 0);
        Check("no settler unit leaves no cursor-slot entry: the cursor index points at nothing",
            settler.CurrentPositionIndex == -1 && settler.ValidTiles[settlerSlot].TileIndex == -1
            && settler.SettleFailure == ArmyActionFailureFlags.None);
        surface.ArmyGateFlags = ArmyActionFailureFlags_Part1.None;

        // INACTIVE in a settler mode (zero picks: every tile water) displays nothing.
        canvas.Started = 0; canvas.Stopped = 0;
        Refresh(settler, 131); // re-publish the admitted set: army gate clean, settlement-free
        plugin.Stars.Render(PresentationView.Current.Picks); // reconcile the tracked fx to the admitted set
        int trackedBeforeZero = PublishedTiles().Length;
        surface.AllWater = true;
        surface.DistrictFrame++;
        Refresh(settler, 131);
        Check("settler zero picks publish INACTIVE", Inactive());
        canvas.Started = 0; canvas.Stopped = 0; // count only the INACTIVE reconcile
        plugin.Stars.Render(PresentationView.Current.Picks);
        Check("settler INACTIVE clears every tracked star",
            canvas.Stopped == trackedBeforeZero && canvas.Started == 0);
        Check("settler INACTIVE shows no pin and leaves no pick entry",
            settlerTiles.Length > 0 && !GateAnswer(settlerTiles[0], true) && !GateAnswer(bestTile, true)
            && ScrubbedAdvisorSlots(settler, baseline, new int[0]) >= 0);
        surface.AllWater = false;
        surface.DistrictFrame++;
        // The mode the pin gate answers for is a published fact now: no row stamps it. The next
        // refresh, in whatever mode it runs, publishes the mode the gate then reads.

        // F. Game surface contract: the membership mask (SPEC section 2 step 6) and the startup
        // name checks for every member the rewrite adds (no partial binding).
        var moneyInfluenceTerrain = (ArmyActionFailureFlags)(ArmyActionFailureFlags_Part1.NotEnoughMoney
            | ArmyActionFailureFlags_Part1.NotEnoughInfluence | ArmyActionFailureFlags_Part1.TerrainNotValid);
        Check("mask drops money and influence, keeps the rest",
            GameApi.ApplyMoneyInfluenceMask(moneyInfluenceTerrain) == (ArmyActionFailureFlags)ArmyActionFailureFlags_Part1.TerrainNotValid);
        Check("mask turns an unaffordable tile into a clean one",
            GameApi.ApplyMoneyInfluenceMask((ArmyActionFailureFlags)ArmyActionFailureFlags_Part1.NotEnoughMoney) == ArmyActionFailureFlags.None);
        var mixedParts = (ArmyActionFailureFlags)ArmyActionFailureFlags_Part1.NotEnoughInfluence
            | (ArmyActionFailureFlags)ArmyActionFailureFlags_Part2.NoValidAdjacentTerritoryToClaim;
        Check("mask never touches Part 2 flags",
            GameApi.ApplyMoneyInfluenceMask(mixedParts) == (ArmyActionFailureFlags)ArmyActionFailureFlags_Part2.NoValidAdjacentTerritoryToClaim);
        Check("mask on a clean gate result stays clean",
            GameApi.ApplyMoneyInfluenceMask(ArmyActionFailureFlags.None) == ArmyActionFailureFlags.None);

        var required = string.Join(" | ", GameApi.RequiredNames);
        Check("startup checks the camp tile gate", required.Contains("ArmyActionHelper.FillCreateCampFailures(tile)"));
        Check("startup checks the camp army gate", required.Contains("ArmyActionHelper.FillCreateCampFailures(army)"));
        Check("startup checks the FoundCity tile gate", required.Contains("ArmyActionHelper.FillFoundCityFailures(position)"));
        Check("startup checks the FoundCity army gate", required.Contains("ArmyActionHelper.FillFoundCityFailures(army)"));
        Check("startup checks the settler tile gate", required.Contains("ArmyActionHelper.FillSettlerAbilityFailures(tile)"));
        Check("startup checks the settler army gate", required.Contains("ArmyActionHelper.FillSettlerAbilityFailures(army)"));
        Check("startup checks both district definitions",
            required.Contains("DepartmentOfTheInterior.CampDefinition") && required.Contains("DepartmentOfTheInterior.CityCenterDefinition"));
        Check("startup checks the explored any-bit predicate, and the all-bits one is gone",
            required.Contains("VisibilityController.IsWorldPositionExplored") && !required.Contains("IsWorldPositionExploredFor"));
        Check("startup checks the tile-to-position conversion the FoundCity gate needs",
            required.Contains("WorldPosition.GetHexagonOffsetFromTileIndex"));
        Check("startup checks the claim-frame source", required.Contains("World.TerritoryInfo"));
        Check("startup checks the snapshot territory facts",
            required.Contains("TerritoryInfo.IsEdgeOfTheWorld") && required.Contains("TerritoryInfo.Claimed")
            && required.Contains("TerritoryInfo.SettlementGUID") && required.Contains("TerritoryInfo.EmpireIndex")
            && required.Contains("WorldSnapshot.TerritoryInfo"));
        Check("startup checks the adjacency and site-tile sources",
            required.Contains("Territory.Adjacencies") && required.Contains("Territory.TileIndexes"));
        Check("startup checks every surface member by exactly one name entry",
            GameApi.RequiredNames.Distinct().Count() == GameApi.RequiredNames.Length);
        // The settler row is wired (ticket 18) and scores on the city-center definition: the only
        // settlement input the settler session reaches is the district definition, never a
        // settlement starting pack (the vanilla evaluator never reads it, SPEC section 3).
        object settlerDefinition;
        Check("the settler row is wired and reaches the city-center definition, no settlement starting pack",
            ModePolicy.IsWired(ArmyActionType.FoundCityWithSettler)
            && new GraphSurface().TryGetDistrictDefinition(ArmyActionType.FoundCityWithSettler, out settlerDefinition)
            && (string)settlerDefinition == "cityCenter"
            && !required.ToLower().Contains("startingpack"));

        // G. The camp pipeline at the takeover (ticket 13). Vanilla's full camp evaluation is
        // skipped, the scan pass publishes its admission set on the baseline the advisor
        // reserves, and the best-of-2 cascade arrays stay retired.
        var scanEval = NewEvaluation(ArmyActionType.Ransack, baseline); // the takeover stamps the mode
        var scanSnapshot = CreateSnapshot();
        bool vanillaRuns = ScanPass(scanSnapshot, scanEval, ArmyActionType.CreateCamp, 131);
        Check("camp takeover skips vanilla's full camp evaluation", !vanillaRuns);
        Check("camp takeover stamps the mode on the evaluation", scanEval.SettleType == ArmyActionType.CreateCamp);
        Check("camp takeover reserves the activation baseline",
            scanEval.ValidTileCount == 0 && scanEval.ValidTiles.Length == 1 + DefaultTailCapacity);
        Check("camp takeover publishes the admission set", !Inactive() && AscendingUnique(PublishedTiles()));
        Check("camp takeover writes no pick entries", ScanTailHits(scanEval, PublishedTiles(), 1) == 0);
        Check("camp takeover leaves the best-camp arrays retired",
            AllMinusOne(scanEval.BestCampPlacementTileIndexes) && AllMinusOne(scanEval.BestCampPlacementIndexes));
        Check("no mod code reads the best-camp array: a populated best tile outside the set shows no pin",
            !GateAnswer(bestTile, true));

        canvas.Started = 0;
        canvas.Stopped = 0;
        plugin.Stars.Render(PresentationView.Current.Picks);
        int trackedStars = canvas.Started;
        int liveStars = trackedStars; // fx the renderer currently tracks, in members of the last set rendered
        Check("the takeover's admission set renders one star per member",
            trackedStars == PublishedTiles().Length);

        // The army-level gate: any hit publishes INACTIVE, writes nothing, asks no tile, and
        // displays nothing: stars cleared, no pin, no vanilla surface taking the advice's place.
        surface.ArmyGateFlags = ArmyActionFailureFlags_Part1.ArmyLocked;
        surface.TileGateCalls = 0;
        Refresh(scanEval, 131);
        Check("army-level gate hit publishes INACTIVE", Inactive());
        canvas.Started = 0;
        canvas.Stopped = 0;
        plugin.Stars.Render(PresentationView.Current.Picks);
        Check("army-level gate hit clears every tracked star",
            canvas.Stopped == trackedStars && canvas.Started == 0);
        Check("army-level gate hit shows no pin and takes no vanilla fallback",
            !GateAnswer(131, true) && !GateAnswer(bestTile, true));

        bool gateCleanTail = scanEval.ValidTileCount <= 1; // the cursor slot, nothing appended
        for (int i = 1; i < scanEval.ValidTiles.Length; i++)
            if (scanEval.ValidTiles[i].TileIndex != -1) gateCleanTail = false;
        Check("army-level gate hit writes no pick entries", gateCleanTail);
        Check("army-level gate hit skips the scan: no tile gate call", surface.TileGateCalls == 0);
        Check("army-level gate hit at the takeover skips vanilla and publishes INACTIVE",
            !ScanPass(scanSnapshot, scanEval, ArmyActionType.CreateCamp, 131) && Inactive());
        surface.ArmyGateFlags = ArmyActionFailureFlags_Part1.None;

        // G1 (F1). The army-level gate now asks the shared army-state rows - ArmyActionHelper
        // .FillCommonArmyFailures: the decoy ActionProhibited, ArmyLockedByBattle and ArmyLocked
        // checks - before the mode's own army rows, because the camp and settler army overloads never
        // run them (SPEC section 2 step 3, SPEC section 3). The harness seam is the surface's
        // TryGetArmyGateFailures: it reports what those rows answer for the mode.
        var commonGateEval = NewEvaluation(ArmyActionType.CreateCamp, baseline);
        plugin.Writer.NoteActivation(commonGateEval);
        snapshot.PresentationData.PlacementEvaluation = commonGateEval;
        var commonGateTail = SlotsSignature(commonGateEval, baseline, commonGateEval.ValidTiles.Length);
        surface.ArmyGateFlags = ArmyActionFailureFlags_Part1.None;
        surface.DistrictFrame++;
        Refresh(commonGateEval, 131); // the camp session the rows start from: an active published set
        canvas.Started = 0;
        canvas.Stopped = 0;
        plugin.Stars.Render(PresentationView.Current.Picks);
        Check("the army-gate rows start from an active published camp set",
            !Inactive() && PresentationView.Current.Mode == ArmyActionType.CreateCamp
            && canvas.Started == PublishedTiles().Length);
        int commonGateStars = canvas.Started;

        // ArmyLockedByBattle: the shared army rows' battle lock, answered in camp mode.
        surface.ArmyGateModes = "";
        surface.TileGateCalls = 0;
        canvas.Started = 0;
        canvas.Stopped = 0;
        surface.ArmyGateFlags = ArmyActionFailureFlags_Part1.ArmyLockedByBattle;
        Refresh(commonGateEval, 131);
        Check("a battle-locked army publishes INACTIVE in camp mode", Inactive());
        Check("a battle-locked army asks no tile gate", surface.TileGateCalls == 0);
        Check("a battle-locked army writes no entries: the tail stays the activation baseline",
            SlotsSignature(commonGateEval, baseline, commonGateEval.ValidTiles.Length) == commonGateTail);
        plugin.Stars.Render(PresentationView.Current.Picks);
        Check("a battle-locked army starts no star and clears every tracked star",
            canvas.Started == 0 && canvas.Stopped == commonGateStars);
        Check("a battle-locked army shows no pin on any tile, the vanilla best tile included",
            !GateAnswer(131, true) && !GateAnswer(132, true) && !GateAnswer(133, true) && !GateAnswer(bestTile, true));
        Check("the camp mode dispatch reaches the army gate: exactly one camp army-gate call",
            surface.ArmyGateModes == "CreateCamp ");

        // ArmyLocked: the shared army rows' lock, answered in camp mode.
        surface.ArmyGateModes = "";
        surface.TileGateCalls = 0;
        canvas.Started = 0;
        canvas.Stopped = 0;
        surface.ArmyGateFlags = ArmyActionFailureFlags_Part1.ArmyLocked;
        Refresh(commonGateEval, 131);
        Check("a locked army publishes INACTIVE in camp mode", Inactive());
        Check("a locked army asks no tile gate", surface.TileGateCalls == 0);
        Check("a locked army writes no entries: the tail stays the activation baseline",
            SlotsSignature(commonGateEval, baseline, commonGateEval.ValidTiles.Length) == commonGateTail);
        plugin.Stars.Render(PresentationView.Current.Picks);
        Check("a locked army starts no star, and none of the published sets has a tracked star left",
            canvas.Started == 0 && canvas.Stopped == 0);
        Check("a locked army shows no pin on any tile, the vanilla best tile included",
            !GateAnswer(131, true) && !GateAnswer(132, true) && !GateAnswer(133, true) && !GateAnswer(bestTile, true));
        Check("the camp mode dispatch reaches the army gate again: exactly one camp army-gate call",
            surface.ArmyGateModes == "CreateCamp ");

        surface.ArmyGateModes = "";
        surface.TileGateCalls = 0;
        Check("the camp scan pass reaches the army gate, skips vanilla and publishes nothing on a locked army",
            !ScanPass(scanSnapshot, commonGateEval, ArmyActionType.CreateCamp, 131) && Inactive()
            && ModeCalls(surface.ArmyGateModes, "CreateCamp") == 1 && surface.TileGateCalls == 0);
        surface.ArmyGateFlags = ArmyActionFailureFlags_Part1.None;
        surface.DistrictFrame++;
        snapshot.PresentationData.PlacementEvaluation = scanEval; // the rows below drive the scan evaluation

        // Unmapped and untaken modes: vanilla stays fully in charge, the advisor publishes INACTIVE.
        var foreignEval = NewEvaluation(ArmyActionType.Ransack, baseline);
        var foreignClean = Signature(foreignEval);
        Check("an unmapped mode leaves vanilla fully in charge", ScanPass(scanSnapshot, foreignEval, ArmyActionType.Ransack, 131));
        Check("an unmapped mode publishes INACTIVE", Inactive());
        Check("an unmapped mode writes nothing", Signature(foreignEval) == foreignClean);

        var cityScan = NewEvaluation(ArmyActionType.Ransack, baseline); // the takeover stamps the mode
        var cityScanSnapshot = CreateSnapshot();
        Check("city founding is taken over: vanilla's full settlement evaluation is skipped",
            !ScanPass(cityScanSnapshot, cityScan, ArmyActionType.FoundCity, 131));
        Check("the city takeover stamps FoundCity on the evaluation",
            cityScan.SettleType == ArmyActionType.FoundCity);
        Check("the city takeover reserves the activation baseline",
            cityScan.ValidTileCount == 0 && cityScan.ValidTiles.Length == 1 + DefaultTailCapacity);
        Check("the city takeover publishes its admission set",
            !Inactive() && AscendingUnique(PublishedTiles()));
        Check("the city takeover writes no pick entries",
            ScanTailHits(cityScan, PublishedTiles(), 1) == 0);
        Check("the city takeover leaves the best-camp arrays retired",
            AllMinusOne(cityScan.BestCampPlacementTileIndexes) && AllMinusOne(cityScan.BestCampPlacementIndexes));

        // The army-level gate in a city mode behaves as it does in camp: any hit publishes INACTIVE,
        // writes nothing, asks no tile, and leaves vanilla's full evaluation unsent.
        surface.ArmyGateFlags = ArmyActionFailureFlags_Part1.AlreadyFoundACity;
        surface.TileGateCalls = 0;
        Check("a city army-gate hit skips vanilla and publishes INACTIVE",
            !ScanPass(cityScanSnapshot, cityScan, ArmyActionType.FoundCity, 131) && Inactive());
        Check("a city army-gate hit asks no tile gate", surface.TileGateCalls == 0);
        surface.ArmyGateFlags = ArmyActionFailureFlags_Part1.None;

        // Admission on the refresh: quota 2 with 1 tile at rank 1 and 2 tiles tied at rank 2
        // publishes 3 (SPEC section 2 step 8), every admitted tile gets a star, and pins follow
        // the published membership.
        surface.ScoreByTile[131] = 90;
        surface.ScoreByTile[132] = 50;
        surface.ScoreByTile[133] = 50;
        surface.DistrictFrame++;
        Refresh(scanEval, 131);
        var admitted = PublishedTiles();
        Check("quota 2 plus the rank-2 tie set publishes 3 tiles",
            admitted.Length == 3 && admitted[0] == 131 && admitted[1] == 132 && admitted[2] == 133);
        canvas.Started = 0;
        canvas.Stopped = 0;
        plugin.Stars.Render(PresentationView.Current.Picks);
        Check("every admitted tile gets a star, uncapped", canvas.Started == admitted.Length);
        liveStars = admitted.Length;
        Check("pins follow the published membership",
            GateAnswer(131) && GateAnswer(132) && GateAnswer(133) && !GateAnswer(121) && !GateAnswer(5001));

        // The two renderings disagree on exactly the rows vanilla's generic pin filters decide:
        // a member with zero FIMS yields and a member off screen both keep their star, and the
        // advisor's gate admits both - the yield reject and the on-screen culling live in the
        // sealed filter chain vanilla owns, which the mod never patches.
        Check("every published member entry carries the zero-yield fact the vanilla yield filter rejects",
            AllMemberEntriesZeroYield(scanEval, admitted, plugin.Writer.CursorSlotIndex(scanEval) + 1));
        Check("vanilla's unpatched filter chain runs, and rejects the writer's scrubbed slot",
            !VanillaPinFilter(ScrubbedEntry()));
        Check("the gate carries no yield rule of its own: every zero-yield member is admitted",
            admitted.Length == 3 && GateAnswer(admitted[0]) && GateAnswer(admitted[1]) && GateAnswer(admitted[2]));

        // A member three territories deep in the placement neighborhood - the far side of the
        // map from the cursor: its star renders, the gate admits it, and the on-screen culling
        // that keeps its pin off screen is vanilla's, unoverridden.
        Plugin.CfgRangeLevels.Value = 3;
        surface.DistrictFrame++;
        Refresh(scanEval, 101);
        var neighborhood = PublishedTiles();
        canvas.Started = 0;
        canvas.Stopped = 0;
        plugin.Stars.Render(PresentationView.Current.Picks);
        Check("a neighborhood member far from the cursor gets its star",
            ContainsTile(neighborhood, 123) && canvas.Started == neighborhood.Length);
        liveStars = neighborhood.Length; // the fx the renderer tracks after this pass
        Check("a neighborhood member far from the cursor is admitted by the gate",
            GateAnswer(123) && !GateAnswer(122));
        Plugin.CfgRangeLevels.Value = 2;
        surface.DistrictFrame++;

        // The tile gate mask on the pipeline path: money and influence stay in, everything else out.
        surface.TileGateFlags[131] = ArmyActionFailureFlags_Part1.NotEnoughMoney;
        surface.DistrictFrame++;
        Refresh(scanEval, 131);
        Check("an unaffordable tile stays in the published set",
            !Inactive() && PublishedTiles().Length == 3 && PresentationView.Current.Contains(131));
        surface.TileGateFlags[131] = ArmyActionFailureFlags_Part1.TileIsOccupied;
        surface.DistrictFrame++;
        Refresh(scanEval, 131);
        Check("a non-cost gate failure drops the tile", !Inactive() && !PresentationView.Current.Contains(131));
        Check("a dropped tile keeps its star off the set, and its pin off", !GateAnswer(131));
        surface.TileGateFlags.Clear();

        // Zero picks: INACTIVE displays nothing - stars cleared, no pin, and no vanilla surface
        // taking the advice's place.
        surface.AllWater = true;
        surface.DistrictFrame++;
        Refresh(scanEval, 131);
        Check("zero picks publish INACTIVE", Inactive());
        canvas.Started = 0;
        canvas.Stopped = 0;
        plugin.Stars.Render(PresentationView.Current.Picks);
        Check("INACTIVE clears every tracked star", canvas.Stopped == liveStars && canvas.Started == 0);
        Check("zero picks show no pin: neither a former member nor a vanilla best tile",
            !GateAnswer(131, true) && !GateAnswer(5001, true));
        surface.AllWater = false;
        liveStars = 0;

        // The resource-tile exclusion is a rule of the pipeline, not of the gate.
        surface.ResourceTiles.Add(133);
        surface.DistrictFrame++;
        Refresh(scanEval, 131);
        Check("the resource tile is excluded from admission", !Inactive() && !PresentationView.Current.Contains(133));
        surface.ResourceTiles.Clear();


        // H. The cursor slot (ticket 16). Every refresh of a taken-over mode writes the TailStart
        // entry itself, from the mode gate call with NO mask: the failure flags and the three
        // instant costs land on the evaluation where the camp mouse marker reads them, the cursor
        // index points at that slot, and vanilla's append-one-tile hover rule never runs. An
        // unaffordable pick is the same entry an affordable one is: star, pin and shown set
        // unchanged, the failure appearing on hover.
        int cursorSlot = plugin.Writer.CursorSlotIndex(scanEval); // 0: this evaluation's activation baseline
        surface.TileGateFlags[131] = ArmyActionFailureFlags_Part1.NotEnoughMoney | ArmyActionFailureFlags_Part1.NotEnoughInfluence;
        surface.TileGateMoney[131] = (FixedPoint)40;
        surface.TileGateInfluence[131] = (FixedPoint)25;
        surface.TileGateUnit[131] = (FixedPoint)8;
        surface.DistrictFrame++;
        int maskedBefore = surface.TileGateCalls, unmaskedBefore = surface.UnmaskedGateCalls;
        bool vanillaRefreshSkipped = !Refresh(scanEval, 131);
        var hoveredSet = PublishedTiles();
        var slotEntry = scanEval.ValidTiles[cursorSlot];
        Check("the advisor owns the refresh: vanilla's whole hover body, append rule included, is skipped",
            vanillaRefreshSkipped);
        Check("the cursor slot comes from the mode gate call with no mask",
            surface.UnmaskedGateCalls > unmaskedBefore && surface.TileGateCalls > maskedBefore);
        Check("the cursor index points at the advisor's own slot, on the hovered tile",
            scanEval.CurrentPositionIndex == cursorSlot && slotEntry.TileIndex == 131);
        Check("the unaffordable hovered tile shows its failure flags and its three instant costs",
            (scanEval.SettleFailure & (ArmyActionFailureFlags)ArmyActionFailureFlags_Part1.NotEnoughMoney) != 0
            && (scanEval.SettleFailure & (ArmyActionFailureFlags)ArmyActionFailureFlags_Part1.NotEnoughInfluence) != 0
            && scanEval.InstantMoneyCost == (FixedPoint)40
            && scanEval.InstantInfluenceCost == (FixedPoint)25
            && scanEval.InstantUnitCost == (FixedPoint)8);
        Check("the cursor slot carries no glyph of its own", !slotEntry.Showable);
        Check("the advisor writes the cursor slot once: the array stays at the reserved window",
            scanEval.ValidTiles.Length == cursorSlot + 1 + DefaultTailCapacity);
        // The writer lands the cursor-slot fields on the live evaluation, where the camp mouse marker
        // and the city ghost read them; the pin gate reads the published copy of the same gate answer.
        snapshot.PresentationData.PlacementEvaluation = scanEval;
        canvas.Started = 0;
        canvas.Stopped = 0;
        plugin.Stars.Render(PresentationView.Current.Picks);
        Check("hovering the unaffordable pick stops no star, and the tile stays a member",
            canvas.Stopped == 0 && ContainsTile(hoveredSet, 131));
        Check("the pick slot entry for the same tile is the affordable pick's shape: shown, scored",
            ShowableIsMembership(scanEval, 0, hoveredSet));
        // The pin gate's hovered rule (SettlementPlacementPinsSubset.cs:29-33) answers from the
        // published cursor-slot failure: the same unmasked gate answer the advisor wrote into the
        // cursor slot. Money drops the pin on hover, influence-only keeps it, a clean hover keeps it.
        // Membership, stars and glyph flags never move.
        Check("the pin gate's hovered rule reads the published cursor-slot failure",
            !GateAnswer(131));
        surface.TileGateFlags[131] = ArmyActionFailureFlags_Part1.NotEnoughInfluence;
        surface.DistrictFrame++;
        Refresh(scanEval, 131);
        Check("an influence-only hover failure keeps the member pin", GateAnswer(131));
        surface.TileGateFlags[131] = ArmyActionFailureFlags_Part1.None;
        surface.DistrictFrame++;
        Refresh(scanEval, 131);
        Check("the clean hover shows no failure flags on the same slot the affordable pick owns",
            scanEval.SettleFailure == ArmyActionFailureFlags.None
            && scanEval.CurrentPositionIndex == cursorSlot
            && scanEval.ValidTiles[cursorSlot].TileIndex == 131
            && GateAnswer(131));
        Check("hovering the pick is the affordable shape: same membership, same star set",
            PublishedTiles().Length == hoveredSet.Length && ShowableIsMembership(scanEval, 0, hoveredSet));
        surface.TileGateFlags.Clear();
        Check("every advisor slot above the live count is zeroed, cursor slot window included",
            ZeroFilledAboveCount(scanEval, scanEval.ValidTileCount, scanEval.ValidTiles.Length));
        var keptVanilla = NewEvaluation(ArmyActionType.Ransack, baseline); // a mode the table has not wired
        var keptSignature = Signature(keptVanilla);
        Check("a mode the table has not wired keeps vanilla's own refresh, its own cursor entry included",
            Refresh(keptVanilla, 5001) && Signature(keptVanilla) == keptSignature && Inactive());

        // A cursor outside the world answers nothing: the cursor slot scrubs and the cursor index
        // points at nothing.
        Refresh(scanEval, 5099);
        Check("a cursor with no territory scrubs the cursor slot and points the index at nothing",
            scanEval.CurrentPositionIndex == -1 && scanEval.ValidTiles[cursorSlot].TileIndex == -1
            && scanEval.SettleFailure == ArmyActionFailureFlags.None);
        // I. Tail capacity from config, full-region scrub, incremental zero-fill, slot stability
        // (ticket 19, SPEC section 6 rows 1-4). The derivation is the worst-case admission set for
        // the configured hops: the neighborhood territory budget (the hovered territory plus 8
        // fan-out territories per hop beyond level 1) times the per-territory worst case (the larger
        // quota plus the 8-slot tie headroom).
        Check("derived tail capacity: quotas 2/1 with 1 hop is 1 territory x (2 + 8)",
            AdvisorConfig.TailCapacityFor(2, 1, 1) == 10);
        Check("derived tail capacity: quotas 2/1 with 2 hops is 9 territories x (2 + 8)",
            AdvisorConfig.TailCapacityFor(2, 1, 2) == DefaultTailCapacity);
        Check("derived tail capacity: quotas 2/1 with 3 hops is 17 territories x (2 + 8)",
            AdvisorConfig.TailCapacityFor(2, 1, 3) == WideTailCapacity);
        Check("derived tail capacity: quotas 5/3 with 2 hops is 9 territories x (5 + 8)",
            AdvisorConfig.TailCapacityFor(5, 3, 2) == RoomyQuotaCapacity);
        Check("derived tail capacity: quotas clamp to 1 and 1 hop is 1 territory x (1 + 8)",
            AdvisorConfig.TailCapacityFor(0, 0, 1) == SingleHopCapacity
            && AdvisorConfig.TailCapacityFor(-3, -1, 0) == SingleHopCapacity);
        Check("derived tail capacity grows with the hop count and with the quota",
            AdvisorConfig.TailCapacityFor(2, 1, 3) > AdvisorConfig.TailCapacityFor(2, 1, 2)
            && AdvisorConfig.TailCapacityFor(5, 1, 2) > AdvisorConfig.TailCapacityFor(2, 1, 2));
        Check("derived tail capacity saturates at the pre-allocation ceiling on a 100000-hop config",
            AdvisorConfig.TailCapacityFor(1, 1, 100000) == ReserveCeiling);

        // The activation reserves the derived window and nothing more: the reserve follows the config
        // the activation reads, so a wider scan or a bigger quota widens the next window.
        var sized = NewEvaluation(ArmyActionType.CreateCamp, baseline);
        plugin.Writer.NoteActivation(sized);
        Check("the activation reserves the config-derived window: quotas 2/1, 2 hops, 6 + 1 + 90",
            sized.ValidTiles.Length == baseline + 1 + DefaultTailCapacity
            && plugin.Writer.CursorSlotIndex(sized) == baseline);

        Plugin.CfgRangeLevels.Value = 3;
        var wide = NewEvaluation(ArmyActionType.CreateCamp, baseline);
        plugin.Writer.NoteActivation(wide);
        Check("a 3-hop config reserves its own derived window: 6 + 1 + 170",
            wide.ValidTiles.Length == baseline + 1 + WideTailCapacity);
        Plugin.CfgRangeLevels.Value = 2;

        Plugin.CfgTopHovered.Value = 5;
        Plugin.CfgTopNeighbour.Value = 3;
        var roomy = NewEvaluation(ArmyActionType.CreateCamp, baseline);
        plugin.Writer.NoteActivation(roomy);
        Check("quotas 5/3 widen the derived window: 6 + 1 + 117",
            roomy.ValidTiles.Length == baseline + 1 + RoomyQuotaCapacity);
        Plugin.CfgTopHovered.Value = 2;
        Plugin.CfgTopNeighbour.Value = 1;

        // Grow-on-demand: an admission set past the derived reserve extends the reserved region, and
        // the mode switch that follows scrubs the whole grown region in every mode - a grown tail
        // left under-scrubbed is exactly the phantom recommendation this row bans.
        Plugin.CfgTopHovered.Value = 1;
        Plugin.CfgTopNeighbour.Value = 1;
        Plugin.CfgRangeLevels.Value = 1;
        var grown = NewEvaluation(ArmyActionType.CreateCamp, baseline); // 38 slots: vanilla's own shape
        var grownTailBefore = SlotsSignature(grown, baseline + 1 + SingleHopCapacity, grown.ValidTiles.Length);
        plugin.Writer.NoteActivation(grown);
        Check("quotas 1/1 with 1 hop reserve the 1-territory window: scrubbed [6, 16), vanilla's tail above it intact",
            ScrubbedRegion(grown, baseline, baseline + 1 + SingleHopCapacity)
            && SlotsSignature(grown, baseline + 1 + SingleHopCapacity, grown.ValidTiles.Length) == grownTailBefore);
        var pastTheReserve = Picks(600, GrownPickCount); // far past the 9-slot reserve
        plugin.Writer.Write(grown, pastTheReserve, AnsweredCursor(600));
        Check("grow-on-demand extends the window to the written slots, the count rising over written slots",
            grown.ValidTiles.Length == baseline + 1 + GrownPickCount
            && grown.ValidTileCount == baseline + 1 + GrownPickCount
            && NoHolesBelowCount(grown, baseline));
        Check("the cursor slot keeps its invariant index through a grown write",
            plugin.Writer.CursorSlotIndex(grown) == baseline && grown.CurrentPositionIndex == baseline);

        grown.SettleType = ArmyActionType.FoundCity;
        grown.ValidTileCount = baseline;
        plugin.Writer.NoteActivation(grown);
        Check("a city activation scrubs the full region a grown camp tail reserved", ReleasedTail(grown, baseline));
        grown.SettleType = ArmyActionType.FoundCityWithSettler;
        grown.ValidTileCount = baseline;
        plugin.Writer.NoteActivation(grown);
        Check("a settler activation scrubs the full region a grown city tail reserved", ReleasedTail(grown, baseline));
        grown.SettleType = ArmyActionType.CreateCamp;
        grown.ValidTileCount = baseline;
        plugin.Writer.NoteActivation(grown);
        Check("a camp activation scrubs the full region a grown settler tail reserved", ReleasedTail(grown, baseline));

        // Incremental zero-fill (SPEC section 6 row 3): a refresh zeroes the slots it released and
        // nothing wider. The slots above the reserved window are vanilla's append space: the writer
        // owns the window, never the array length, so they survive every refresh.
        var sweep = NewEvaluation(ArmyActionType.CreateCamp, baseline);
        GrowWithSentinels(sweep, baseline + 1 + SingleHopCapacity + 120);
        var aboveWindowBefore = SlotsSignature(sweep, baseline + 1 + SingleHopCapacity, sweep.ValidTiles.Length);
        var vanillaGlyphsBefore = VanillaGlyphs(sweep, baseline);
        plugin.Writer.NoteActivation(sweep); // a 16-slot window inside a 136-slot array
        Check("reserving a window inside a longer array zeroes the window, not the array",
            ScrubbedRegion(sweep, baseline, baseline + 1 + SingleHopCapacity)
            && SlotsSignature(sweep, baseline + 1 + SingleHopCapacity, sweep.ValidTiles.Length) == aboveWindowBefore
            && VanillaGlyphs(sweep, baseline) == vanillaGlyphsBefore);
        plugin.Writer.Write(sweep, Picks(700, SingleHopCapacity), AnsweredCursor(700)); // the window, full
        plugin.Writer.Write(sweep, Picks(700, 3), AnsweredCursor(700)); // 6 slots released
        Check("a refresh zeroes exactly the slots it released",
            ScrubbedRegion(sweep, baseline + 1 + 3, baseline + 1 + SingleHopCapacity));
        Check("no full-array sweep: vanilla's slots above the reserved window survive every refresh",
            SlotsSignature(sweep, baseline + 1 + SingleHopCapacity, sweep.ValidTiles.Length) == aboveWindowBefore
            && VanillaGlyphs(sweep, baseline) == vanillaGlyphsBefore);

        // Slot stability (SPEC section 6 row 2): the pick slots are the indices vanilla's pins bind
        // to, so a recomputed set lands on the same slots and a retained pick never rebinds.
        var stable = NewEvaluation(ArmyActionType.CreateCamp, baseline);
        plugin.Writer.NoteActivation(stable);
        surface.DistrictFrame++;
        Refresh(stable, 131);
        var stableFirst = PublishedTiles();
        int stableSlot = plugin.Writer.CursorSlotIndex(stable);
        int stableLength = stable.ValidTiles.Length;
        Check("the first refresh writes each pick at its invariant slot after the cursor slot",
            SlotIndexOf(stable, stableFirst, stableSlot + 1) == InvariantSlots(stableFirst, stableSlot + 1));
        surface.DistrictFrame++; // a cache-miss refresh of the same admission set
        Refresh(stable, 131);
        Check("retained picks keep their slot indices across refreshes, and the cursor slot keeps its index",
            plugin.Writer.CursorSlotIndex(stable) == stableSlot
            && stable.ValidTiles.Length == stableLength
            && stable.CurrentPositionIndex == stableSlot
            && SlotIndexOf(stable, PublishedTiles(), stableSlot + 1)
               == InvariantSlots(PublishedTiles(), stableSlot + 1));
        Plugin.CfgTopHovered.Value = 2;
        Plugin.CfgTopNeighbour.Value = 1;
        Plugin.CfgRangeLevels.Value = 2;

        // J. Config surface and load failure (ticket 21, SPEC section 8): exactly three knobs with
        // the spec's help strings, a hard loud load failure, and a startup summary that names the
        // resolved members and the derived tail capacity.
        var cfg = GeneratedCfg();
        var cfgKeys = CfgKeys(cfg);
        Check("the shipped config surface is exactly TopHovered, TopNeighbour and RangeLevels",
            cfgKeys.Count == 3 && cfgKeys.Contains("TopHovered") && cfgKeys.Contains("TopNeighbour")
            && cfgKeys.Contains("RangeLevels"));
        Check("no enable toggle, score floor, tie cap, auto-disable knob or land-adjacency wording survives",
            !cfgKeys.Contains("Enabled") && !cfgKeys.Contains("MinScore") && !cfgKeys.Contains("ScoreFloor")
            && !cfgKeys.Contains("TieCap") && !cfgKeys.Contains("MaxStars") && !cfgKeys.Contains("AutoDisable")
            && cfg.IndexOf("adjacent land") < 0 && cfg.IndexOf("land-adjacent") < 0 && cfg.IndexOf("land adjacency") < 0);
        Check("TopHovered: default 2 with the spec's exact help string",
            CfgEntry(cfg, "TopHovered").Contains(SpecHelpTopHovered)
            && CfgEntry(cfg, "TopHovered").Contains("# Default value: 2")
            && CfgEntry(cfg, "TopHovered").Contains("TopHovered = 2"));
        Check("TopNeighbour: default 1 with the spec's exact help string",
            CfgEntry(cfg, "TopNeighbour").Contains(SpecHelpTopNeighbour)
            && CfgEntry(cfg, "TopNeighbour").Contains("# Default value: 1")
            && CfgEntry(cfg, "TopNeighbour").Contains("TopNeighbour = 1"));
        Check("RangeLevels: default 2 with the spec's exact help string",
            CfgEntry(cfg, "RangeLevels").Contains(SpecHelpRangeLevels)
            && CfgEntry(cfg, "RangeLevels").Contains("# Default value: 2")
            && CfgEntry(cfg, "RangeLevels").Contains("RangeLevels = 2"));
        var floorContext = CursorContext.For(1, "army", 0, 0, -3, 0, ArmyActionType.CreateCamp);
        Check("every knob clamps below 1 to 1: the quotas and the hop count are mandatory",
            floorContext.TopHovered == 1 && floorContext.TopNeighbour == 1 && floorContext.RangeLevels == 1);
        var unboundedContext = CursorContext.For(1, "army", 0, 2, 1, 100000, ArmyActionType.CreateCamp);
        Check("the hop count has no upper bound: a 100000-hop config reaches the scan unclamped",
            unboundedContext.RangeLevels == 100000);

        var shippedLine = LoadGate.ReadyLine(GameApi.RequiredNames, AdvisorConfig.TailCapacityFor(2, 1, 2));
        var namedMembers = true;
        for (int i = 0; i < GameApi.RequiredNames.Length; i++)
            namedMembers &= shippedLine.Contains(GameApi.RequiredNames[i]);
        Check("startup names every resolved member and the derived tail capacity for the shipped defaults",
            namedMembers && shippedLine.Contains("90"));
        Check("the startup tail capacity follows the config: quotas 2/1 with 3 hops is 170",
            LoadGate.ReadyLine(GameApi.RequiredNames, AdvisorConfig.TailCapacityFor(2, 1, 3)).Contains("170"));

        // K (F3, SPEC section 1 row 16): mode exit takes the channel with the cursor. The published
        // set belongs to the mode the cursor is in, so leaving that mode - publishing inactive for it,
        // exactly what the DeactivateCursor postfix does - must not leave a stale set gating pins.
        var exitEval = NewEvaluation(ArmyActionType.CreateCamp, baseline);
        plugin.Writer.NoteActivation(exitEval);
        snapshot.PresentationData.PlacementEvaluation = exitEval;
        surface.ArmyGateFlags = ArmyActionFailureFlags_Part1.None;
        surface.AllWater = false; surface.AllOcean = false;
        surface.TileGateFlags.Clear();
        surface.ScoreByTile[131] = 90; surface.ScoreByTile[132] = 50; surface.ScoreByTile[133] = 50;
        surface.DistrictFrame++;
        Refresh(exitEval, 131);
        var exitMembers = PublishedTiles();
        Check("the mode-exit row starts from a published camp set that gates its members",
            !Inactive() && PresentationView.Current.Mode == ArmyActionType.CreateCamp
            && exitMembers.Length > 0 && GateAnswer(exitMembers[0], true) && !GateAnswer(bestTile, true));
        PresentationView.PublishInactive(ArmyActionType.CreateCamp); // the cursor-deactivation publish
        Check("mode exit leaves no pin on a former member: a stale set gates nothing",
            exitMembers.Length > 0 && !GateAnswer(exitMembers[0], true));
        Check("mode exit leaves no pin on the vanilla best tile", !GateAnswer(bestTile, true));
        Check("mode exit publishes the inactive content for the mode the cursor left",
            Inactive() && PresentationView.Current.Mode == ArmyActionType.CreateCamp);

        // The wired mode the cursor enters next publishes its own set on the same channel, and pins
        // follow that set: the exited mode's members are out of the gate.
        var exitCity = NewEvaluation(ArmyActionType.FoundCity, baseline);
        plugin.Writer.NoteActivation(exitCity);
        surface.ScoreByTile[131] = 10; surface.ScoreByTile[132] = 7; surface.ScoreByTile[133] = 5;
        surface.DistrictFrame++;
        Refresh(exitCity, 130);
        var exitCityMembers = PublishedTiles();
        Check("the next wired mode republishes the channel from its own refresh",
            PresentationView.Current.Mode == ArmyActionType.FoundCity && !Inactive()
            && exitCityMembers.Length == 2);
        Check("pins follow the new mode's set, not the exited mode's",
            GateAnswer(131, true) && GateAnswer(132, true) && !GateAnswer(133, true)
            && !GateAnswer(bestTile, true));

        // An unwired mode's exit leaves vanilla's own gate answer standing: the channel owns no mode
        // the table has not wired.
        PresentationView.PublishInactive(ArmyActionType.Ransack);
        Check("PublishInactive for an unwired mode leaves vanilla's gate answer on", GateAnswer(bestTile, true));
        Check("PublishInactive for an unwired mode leaves it off when vanilla answers off",
            !GateAnswer(bestTile, false));

        // The real deactivation postfix, on the presentation thread: stars cleared, channel out of
        // the mode. A renderer of its own tracks exactly this set's fx, so the clear is countable.
        Refresh(exitEval, 131); // the camp session live again: a published set and its stars
        var exitCanvas = new CountingCanvas();
        plugin.Stars = new StarRenderer(exitCanvas);
        plugin.Stars.Render(PresentationView.Current.Picks);
        int exitTracked = exitCanvas.Started;
        exitCanvas.Started = 0;
        exitCanvas.Stopped = 0;
        var deactivatePostfix = typeof(CursorDeactivatePatch).GetMethod("Postfix",
            BindingFlags.NonPublic | BindingFlags.Static);
        deactivatePostfix.Invoke(null, null);
        Check("the deactivate postfix stops every tracked star and starts none",
            exitCanvas.Stopped == exitTracked && exitCanvas.Started == 0);
        Check("the deactivate postfix takes the channel out of the mode with the cursor",
            Inactive() && PresentationView.Current.Mode == ArmyActionType.CreateCamp
            && exitMembers.Length > 0 && !GateAnswer(exitMembers[0], true) && !GateAnswer(bestTile, true));
        snapshot.PresentationData.PlacementEvaluation = scanEval;

        // L (F5, SPEC section 1 + section 5 row 9): a pipeline throw never falls back to vanilla. The
        // takeover's catch path hands the evaluation to PublishEmpty: the advisor's contribution is
        // dropped, every advisor slot is the shape vanilla skips, the advisor's own glyphs are gone,
        // and the channel goes INACTIVE - vanilla's own entries stay, vanilla's body is not re-run.
        var thrown = NewEvaluation(ArmyActionType.CreateCamp, baseline);
        var thrownVanilla = VanillaGlyphs(thrown, baseline);
        plugin.Writer.NoteActivation(thrown);
        surface.DistrictFrame++;
        Refresh(thrown, 131); // a normal advisor write first: the tail and the channel are the advisor's
        int thrownSlot = plugin.Writer.CursorSlotIndex(thrown);
        Check("the pipeline-failure row starts from a normal advisor write",
            !Inactive() && thrown.ValidTileCount > baseline + 1
            && thrown.ValidTiles[thrownSlot].TileIndex == 131);
        plugin.Advisor.PublishEmpty(thrown); // the catch path's call, in a taken-over mode
        // The fix changed this row's shape: ClearTail drops the contribution by scrubbing every
        // advisor slot, and Write keeps ValidTileCount where the last write left it - vanilla scores
        // ValidTiles[0..Length) and skips the Initialize(-1) shape, so a scrubbed slot inside the
        // count is inert. "Back to the baseline" is therefore: the cursor index points at nothing, no
        // advisor entry is live anywhere in the count, and the array is still the reserved window.
        Check("a pipeline throw drops the advisor's contribution to the baseline: no advisor entry is live",
            thrown.CurrentPositionIndex == -1
            && ScrubbedAdvisorSlots(thrown, baseline, new int[0]) == thrown.ValidTileCount - baseline - 1
            && thrown.ValidTiles.Length == thrownSlot + 1 + DefaultTailCapacity);
        Check("every advisor slot is zeroed, the cursor slot's own entry with it",
            ZeroFilledAboveCount(thrown, thrownSlot, thrown.ValidTiles.Length)
            && thrown.ValidTiles[thrownSlot].TileIndex == -1 && !thrown.ValidTiles[thrownSlot].Showable);
        Check("the pipeline throw leaves no advisor glyph shown in its slots",
            ShowableIsMembership(thrown, baseline, new int[0]));
        Check("the pipeline throw leaves the channel INACTIVE and shows no pin",
            Inactive() && !GateAnswer(131, true) && !GateAnswer(bestTile, true));
        Check("a pipeline throw keeps vanilla's own entries exactly as vanilla left them",
            VanillaGlyphs(thrown, baseline) == thrownVanilla);
        surface.ThrowAtTheGate = true; // a pipeline that throws mid-pass
        var thrownSignature = Signature(thrown);
        Check("a throwing pipeline skips vanilla's refresh body and publishes INACTIVE",
            !Refresh(thrown, 131) && Inactive() && Signature(thrown) == thrownSignature);
        Check("a throwing pipeline at the takeover skips vanilla's evaluation and publishes INACTIVE",
            !ScanPass(scanSnapshot, thrown, ArmyActionType.CreateCamp, 131) && Inactive()
            && ReleasedTail(thrown, plugin.Writer.CursorSlotIndex(thrown)));
        surface.ThrowAtTheGate = false;
        // The scan path carries vanilla's activation, so the takeover above re-baselined this
        // evaluation: the rows below read the state the takeover left, and must not move it.
        var thrownAfterTakeover = Signature(thrown);
        surface.ReadyFlag = false; // a surface that cannot answer at all
        plugin.Advisor.Run(thrown, 131, new SimulationEntityGUID(1UL));
        Check("an unanswering surface publishes INACTIVE and writes nothing (the refresh entry)",
            Inactive() && Signature(thrown) == thrownAfterTakeover);
        plugin.Advisor.RunScanPass(thrown, 131, new SimulationEntityGUID(1UL));
        Check("an unanswering surface publishes INACTIVE and writes nothing (the scan entry)",
            Inactive() && Signature(thrown) == thrownAfterTakeover);
        surface.ReadyFlag = true;

        // The load gate on an unresolved surface: log the failure list, then close the game, and
        // never hand control back to the advisor wiring. The termination mechanism is injected, so
        // the harness drives the same decision the composition root takes in the game.
        var gateEvents = new List<string>();
        var gateLog = new List<string>();
        var gateReturned = false;
        var gateThrown = "";
        var durablePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "noopcircuit-load-failure.log");
        var durableAtClose = false;
        System.IO.File.Delete(durablePath);
        GameApi.ReadyStatic = false;
        GameApi.ResolveError = "SimulationEvaluator.RenamedMember; ArmyActionHelper.FillCreateCampFailures(tile)";
        try
        {
            LoadGate.EnforceResolvedSurface(
                message =>
                {
                    gateEvents.Add("log");
                    gateLog.Add(message);
                    LoadGate.AppendFailureLine(durablePath, message);
                },
                () =>
                {
                    gateEvents.Add("close");
                    durableAtClose = System.IO.File.ReadAllText(durablePath)
                        .Contains("SimulationEvaluator.RenamedMember");
                });
            gateReturned = true;
        }
        catch (Exception e) { gateThrown = e.Message; }
        Check("unresolved members log the failure list before the game is closed",
            gateEvents.Count == 2 && gateEvents[0] == "log" && gateEvents[1] == "close");
        Check("the logged failure names every unresolved member",
            gateLog.Count == 1 && gateLog[0].Contains("SimulationEvaluator.RenamedMember")
            && gateLog[0].Contains("ArmyActionHelper.FillCreateCampFailures(tile)"));
        Check("the failure list is durable in the mod's own log file by the time the game closes",
            durableAtClose);
        Check("load failure is not a disable: control never returns to the advisor wiring",
            !gateReturned && gateThrown.Contains("SimulationEvaluator.RenamedMember"));

        var readyEvents = new List<string>();
        var readyLog = new List<string>();
        GameApi.ReadyStatic = true;
        GameApi.ResolveError = string.Empty;
        LoadGate.EnforceResolvedSurface(message => readyLog.Add(message), () => readyEvents.Add("close"));
        Check("a resolved surface loads: nothing logged, the game stays open",
            readyEvents.Count == 0 && readyLog.Count == 0);

        // F2 (startup coverage): the mode-policy table's shared army-state row is a startup-bound
        // name like every gate the patch surface binds, so a build that renamed it must fail the load
        // gate in exactly the shape the rows above fail it: log, close, and never a partial binding.
        var commonGateEvents = new List<string>();
        var commonGateLog = new List<string>();
        var commonGateReturned = false;
        var commonGateThrown = "";
        GameApi.ReadyStatic = false;
        GameApi.ResolveError = ModePolicy.CommonArmyGateLabel + "; " + ModePolicy.SettlerArmyGateLabel;
        try
        {
            LoadGate.EnforceResolvedSurface(
                message => { commonGateEvents.Add("log"); commonGateLog.Add(message); },
                () => commonGateEvents.Add("close"));
            commonGateReturned = true;
        }
        catch (Exception e) { commonGateThrown = e.Message; }
        Check("an unresolved shared army-state row fails the load gate: logged, closed, never a partial binding",
            commonGateEvents.Count == 2 && commonGateEvents[0] == "log" && commonGateEvents[1] == "close"
            && !commonGateReturned && commonGateThrown.Contains(ModePolicy.CommonArmyGateLabel));
        Check("the load-failure line names every unresolved member, the shared army row included",
            commonGateLog.Count == 1 && commonGateLog[0].Contains(ModePolicy.CommonArmyGateLabel)
            && commonGateLog[0].Contains(ModePolicy.SettlerArmyGateLabel)
            && commonGateLog[0].StartsWith("Load failure, unresolved game members: "));
        Check("the startup table carries the shared army-state row exactly once, and the ready line names it",
            GameApi.RequiredNames.Count(name => name == ModePolicy.CommonArmyGateLabel) == 1
            && LoadGate.ReadyLine(GameApi.RequiredNames, AdvisorConfig.TailCapacityFor(2, 1, 2))
                .Contains(ModePolicy.CommonArmyGateLabel));
        GameApi.ReadyStatic = true;
        GameApi.ResolveError = string.Empty;

        Console.WriteLine("[noopc] verdict: " + (s_Failures == 0 ? "GREEN" : "RED (" + s_Failures + " contract violations)"));
        Console.WriteLine("[noopc] " + (s_Failures == 0
            ? "rows checked: " + s_Checks
            : s_Failures + " of " + s_Checks + " rows violated"));
        Console.WriteLine(s_Failures == 0 ? "[noopc] NOOP PASS" : "[noopc] NOOP FAIL");
        return s_Failures == 0 ? 0 : 1;
    }

    /// <summary>Vanilla-owned state only: the slot window vanilla iterates, plus the shape
    /// fields. Slots at or above the activation baseline are the advisor's own space.</summary>
    private static string VanillaSlots(CampPlacementEvaluation e, int baseline)
    {
        var sb = new StringBuilder();
        sb.Append("count=").Append(e.ValidTileCount)
          .Append(" pos=").Append(e.CurrentPositionIndex)
          .Append(" len=").Append(e.ValidTiles.Length)
          .Append(" settle=").Append(e.SettleType);
        for (int i = 0; i < baseline; i++)
            sb.Append('|').Append(e.ValidTiles[i].TileIndex).Append(e.ValidTiles[i].Showable ? 'S' : '-');
        return sb.ToString();
    }

    /// <summary>
    /// Headless snapshot instance, flagged as refreshed, so the gate reaches the vanilla best
    /// array path (without this flag the gate early-returns and the test would be vacuous).
    /// </summary>
    private static SettlementPlacementCursorSnapshot CreateSnapshot()
    {
        var snapshot = (SettlementPlacementCursorSnapshot)Activator.CreateInstance(typeof(SettlementPlacementCursorSnapshot));
        SetFlag(snapshot, "enabled", true);
        SetFlag(snapshot, "syncAskedByPresentation", false);
        SetFlag(snapshot, "forcePresentationChanged", false);
        if (!snapshot.HasBeenRefreshedSinceStarted)
            throw new Exception("snapshot not refreshed: the pin gate test would be vacuous");
        return snapshot;
    }

    private static void SetFlag(object target, string field, bool value)
    {
        for (var type = target.GetType(); type != null; type = type.BaseType)
        {
            var f = type.GetField(field, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (f != null) { f.SetValue(target, value); return; }
        }
        throw new Exception("snapshot flag not found: " + field);
    }

    /// <summary>Picks the pipeline never saw, written straight through the writer's public entry
    /// point: grow-on-demand is unreachable from the 4-territory synthetic map.</summary>
    private static Pick[] Picks(int firstTile, int count)
    {
        var picks = new Pick[count];
        for (int i = 0; i < count; i++)
        {
            var entry = default(DistrictPlacementEvaluation.ValidTile);
            entry.Initialize(firstTile + i);
            picks[i].Tile = firstTile + i;
            picks[i].Score = (FixedPoint)(1000 - i);
            picks[i].Data = entry;
        }
        return picks;
    }

    /// <summary>An answering cursor slot for a direct writer call.</summary>
    private static CursorSlot AnsweredCursor(int tileIndex)
    {
        var entry = default(DistrictPlacementEvaluation.ValidTile);
        entry.Initialize(tileIndex);
        var slot = new CursorSlot();
        slot.Answered = true;
        slot.Tile = tileIndex;
        slot.Entry = entry;
        return slot;
    }

    /// <summary>The array grown to `length` with vanilla's sentinel shape in every new slot, so a
    /// write, a scrub or a sweep into vanilla's append space is visible in the signature.</summary>
    private static void GrowWithSentinels(CampPlacementEvaluation e, int length)
    {
        var tiles = e.ValidTiles;
        Array.Resize(ref tiles, length);
        for (int i = e.ValidTiles.Length; i < length; i++)
        {
            var entry = default(DistrictPlacementEvaluation.ValidTile);
            entry.Initialize(-1000 - i);
            tiles[i] = entry;
        }
        e.ValidTiles = tiles;
        var flags = e.ConditionalFlagsTileIndexes;
        Array.Resize(ref flags, length);
        e.ConditionalFlagsTileIndexes = flags;
    }

    /// <summary>The tile-and-glyph shape of slots [from, to): the slots above the writer's window.</summary>
    private static string SlotsSignature(CampPlacementEvaluation e, int from, int to)
    {
        var sb = new StringBuilder();
        for (int i = from; i < System.Math.Min(to, e.ValidTiles.Length); i++)
            sb.Append('|').Append(e.ValidTiles[i].TileIndex).Append(e.ValidTiles[i].Showable ? 'S' : '-');
        return sb.ToString();
    }

    /// <summary>Every slot in [from, to) is the scrubbed shape: no tile, no glyph.</summary>
    private static bool ScrubbedRegion(CampPlacementEvaluation e, int from, int to)
    {
        for (int i = from; i < System.Math.Min(to, e.ValidTiles.Length); i++)
            if (e.ValidTiles[i].TileIndex != -1 || e.ValidTiles[i].Showable) return false;
        return true;
    }

    /// <summary>No scrubbed slot below the live count in the advisor's slots: the count rises only
    /// over written entries (SPEC section 6 row 2).</summary>
    private static bool NoHolesBelowCount(CampPlacementEvaluation e, int baseline)
    {
        for (int i = baseline + 1; i < e.ValidTileCount; i++)
            if (e.ValidTiles[i].TileIndex == -1) return false;
        return true;
    }

    /// <summary>The slot each member occupies, as a tile@slot list.</summary>
    private static string SlotIndexOf(CampPlacementEvaluation e, int[] members, int from)
    {
        var sb = new StringBuilder();
        for (int t = 0; t < members.Length; t++)
        {
            var at = -1;
            for (int i = from; i < e.ValidTiles.Length; i++)
                if (e.ValidTiles[i].TileIndex == members[t]) { at = i; break; }
            sb.Append('|').Append(members[t]).Append('@').Append(at);
        }
        return sb.ToString();
    }

    /// <summary>The slots SPEC section 6 row 2 fixes: one per pick, in the published order,
    /// starting at the slot after the cursor slot.</summary>
    private static string InvariantSlots(int[] members, int first)
    {
        var sb = new StringBuilder();
        for (int t = 0; t < members.Length; t++)
            sb.Append('|').Append(members[t]).Append('@').Append(first + t);
        return sb.ToString();
    }
}
