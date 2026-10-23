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
    public Dictionary<int, ArmyActionFailureFlags_Part1> TileGateFlags = new Dictionary<int, ArmyActionFailureFlags_Part1>();
    public int TileGateCalls;
    public int UnmaskedGateCalls; // the cursor slot asks unmasked: money and influence stay in
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
        facts.HasSettlement = false;
        facts.OwnerEmpireIndex = -1;
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
/// DEBUG [noopc] shown-set contract harness. All three settle types share
/// CampPlacementEvaluation, so a city session must see zero writes. Checks compare a full
/// signature of the mutable state before and after, catching any mutation, not just known
/// ones. Camp placement is the control: tail reserved, picks written Showable, vanilla's own
/// glyphs untouched, stars and the pin gate answering the published membership. The pin gate is
/// driven through the real postfix, and vanilla's unpatched pin filter chain is driven directly.
/// </summary>
internal static class Program
{
    private static int s_Failures;

    private static void Check(string name, bool ok)
    {
        Console.WriteLine("[noopc] " + (ok ? "OK   " : "FAIL ") + name + (ok ? "" : "  [set: " + DescribeSet() + "]"));
        if (!ok) s_Failures++;
    }

    private static string DescribeSet()
    {
        var set = PickSet.Current;
        if (set == null) return "no set published";
        if (set.Tiles == null) return "INACTIVE";
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
        return PickSet.Current == null || PickSet.Current.Tiles == null;
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
    /// Every advisor-owned slot above the live count - the cursor slot plus TailCapacity slots, the
    /// window the writer reserves - is the zeroed shape: no tile, no FIMS, no glyph. SPEC section 6
    /// row 3, the advisor's duty from the reservation on.
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
    /// Stamp the placement mode the pin gate reads. In production the takeover stamps the mode
    /// on the snapshot's own placement evaluation (SettlementPlacementCursorSnapshot.cs:43),
    /// which is the same evaluation vanilla's gate reads, so the harness stamps the same field.
    /// </summary>
    private static void SetPresentationMode(ArmyActionType mode)
    {
        Snapshots.SettlementPlacementCursorSnapshot.PresentationData.PlacementEvaluation.SettleType = mode;
    }

    /// <summary>The cursor-slot gate result vanilla's hovered rule reads.</summary>
    private static void SetPresentationFailure(ArmyActionFailureFlags failure)
    {
        Snapshots.SettlementPlacementCursorSnapshot.PresentationData.PlacementEvaluation.SettleFailure = failure;
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

    private static int Main()
    {
        var surface = new GraphSurface();
        var plugin = new Plugin();
        plugin.Writer = new EvaluationWriter();
        var canvas = new CountingCanvas();
        plugin.Stars = new StarRenderer(canvas);
        plugin.Advisor = new Advisor(surface, new PickComputation(surface), plugin.Writer, plugin.Stars);
        Plugin.Instance = plugin;
        const int baseline = 6;

        // A. FoundCity: the advisor never touches the evaluation, in any entry point.
        var city = NewEvaluation(ArmyActionType.FoundCity, baseline);
        var clean = Signature(city);

        plugin.Writer.NoteActivation(city);
        Check("FoundCity activation: no resize, no tail claim", Signature(city) == clean);

        Refresh(city, 5001);
        Check("FoundCity refresh: zero writes to the evaluation", Signature(city) == clean);
        Check("FoundCity refresh: vanilla top-2 glyphs keep their Showable flag",
            city.ValidTiles[0].Showable && city.ValidTiles[1].Showable);
        Check("FoundCity refresh: pick set inactive", Inactive());

        int version = PickSet.Current == null ? 0 : PickSet.Current.Version;
        Refresh(city, 5002);
        Refresh(city, 5003);
        Check("FoundCity: repeated refreshes publish nothing and mutate nothing",
            (PickSet.Current == null ? 0 : PickSet.Current.Version) == version && Signature(city) == clean);

        var stray = new Pick { Tile = 7777, Score = 9, Data = city.ValidTiles[0] };
        plugin.Writer.Write(city, new[] { stray }, CursorSlot.Unanswered);
        Check("FoundCity: Write is a no-op", Signature(city) == clean);
        plugin.Writer.ClearTail(city);
        Check("FoundCity: ClearTail is a no-op", Signature(city) == clean);

        // FoundCityWithSettler is the same shared evaluation type: same no-op.
        var settler = NewEvaluation(ArmyActionType.FoundCityWithSettler, baseline);
        var settlerClean = Signature(settler);
        plugin.Writer.NoteActivation(settler);
        Refresh(settler, 5002);
        plugin.Writer.ClearTail(settler);
        Check("FoundCityWithSettler: zero writes", Signature(settler) == settlerClean);

        // B. Camp control: the shown set. Stars and glyphs are the published membership; the
        // writer owns the tail slots only, and vanilla's own glyph flags are its to keep.
        var settlement = NewEvaluation(ArmyActionType.CreateCamp, baseline);
        var settlementGlyphs = VanillaGlyphs(settlement, baseline);
        plugin.Writer.NoteActivation(settlement);
        Check("CreateCamp activation reserves the tail window",
            settlement.ValidTiles.Length >= baseline + 1 + EvaluationWriter.TailCapacity);
        Check("the cursor slot is the activation baseline",
            plugin.Writer.CursorSlotIndex(settlement) == baseline);

        Refresh(settlement, 131); // cursor tile inside the synthetic territory space (territory 3)
        var tiles = PickSet.Current == null || PickSet.Current.Tiles == null ? new int[0] : PickSet.Current.Tiles;
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
        plugin.Stars.Render(PickSet.Current);
        Check("settlement pick set starts one star per pick", canvas.Started == tiles.Length);

        // The tail slots the cursor hovers outside any pick: the refresh writes the cursor slot
        // itself and points the cursor index at it, and the slots this refresh releases zero-fill
        // above the live count (SPEC section 6 row 3).
        Check("every advisor slot above the live count is zeroed",
            ZeroFilledAboveCount(settlement, settlement.ValidTileCount,
                plugin.Writer.CursorSlotIndex(settlement) + 1 + EvaluationWriter.TailCapacity));

        // A smaller admission set releases slots. The writer zero-fills what it released, so
        // inside the live count the published membership stays the only live entry set.
        surface.TileGateFlags[132] = ArmyActionFailureFlags_Part1.TileIsOccupied;
        surface.TileGateFlags[133] = ArmyActionFailureFlags_Part1.TileIsOccupied;
        surface.DistrictFrame++;
        Refresh(settlement, 131);
        var shrunk = PickSet.Current.Tiles;
        plugin.Stars.Render(PickSet.Current);
        Check("a smaller pick set scrubs the slots it released",
            shrunk.Length < tiles.Length
            && ShowableIsMembership(settlement, baseline, shrunk)
            && ScrubbedAdvisorSlots(settlement, baseline, shrunk) >= tiles.Length - shrunk.Length);
        surface.TileGateFlags.Clear();
        surface.DistrictFrame++;
        Refresh(settlement, 131);
        Check("the restored pick set re-occupies its invariant slots",
            ShowableIsMembership(settlement, baseline, PickSet.Current.Tiles));
        plugin.Stars.Render(PickSet.Current);

        // C. Pin gate through the real postfix: published-set membership plus vanilla's hovered
        // rule, answered from the mode the takeover stamps on the snapshot's own evaluation.
        var snapshot = CreateSnapshot();
        int bestTile = 5001; // a vanilla best-2 tile: never an advisor pick (picks are 13x tiles)
        snapshot.PresentationData.PlacementEvaluation.BestCampPlacementTileIndexes = new[] { bestTile, -1 };
        Snapshots.SettlementPlacementCursorSnapshot = snapshot;
        SetPresentationMode(ArmyActionType.CreateCamp);
        SetPresentationFailure(ArmyActionFailureFlags.None);

        int pickTile = tiles[0];
        Gate("camp: a member shows its pin", true, pickTile, true);
        Gate("camp: a member shows its pin with vanilla answering off", false, pickTile, true);
        Gate("camp: the vanilla best tile outside the set shows no pin", true, bestTile, false);
        SetPresentationFailure((ArmyActionFailureFlags)ArmyActionFailureFlags_Part1.NotEnoughInfluence);
        Gate("camp: an influence-only cursor failure keeps the member pin", true, pickTile, true);
        SetPresentationFailure((ArmyActionFailureFlags)ArmyActionFailureFlags_Part1.NotEnoughMoney);
        Gate("camp: a money cursor failure drops the member pin", true, pickTile, false);
        SetPresentationFailure((ArmyActionFailureFlags)ArmyActionFailureFlags_Part1.ArmyLocked);
        Gate("camp: any other cursor failure drops the member pin", true, pickTile, false);
        SetPresentationFailure(ArmyActionFailureFlags.None);

        canvas.Started = 0;
        canvas.Stopped = 0;
        plugin.Stars.Render(PickSet.Current);
        Check("settlement pick set re-render is version-gated (no fx churn)", canvas.Started == 0);
        plugin.Advisor.Deactivate();
        plugin.Stars.Render(PickSet.Current);
        Check("deactivation stops every tracked star", canvas.Stopped == tiles.Length && canvas.Started == 0);
        Check("Deactivate publishes the Tiles==null inactive representation",
            PickSet.Current != null && PickSet.Current.Tiles == null);
        Gate("camp INACTIVE: the vanilla best tile shows no pin", true, bestTile, false);
        Gate("camp INACTIVE: a former pick tile shows no pin", true, pickTile, false);

        int deactivatedVersion = PickSet.Current.Version;
        plugin.Advisor.Deactivate();
        Check("Deactivate is idempotent (no version churn per refresh)",
            PickSet.Current.Version == deactivatedVersion && PickSet.Current.Tiles == null);

        PickSet.Current = null;
        Gate("camp before any publish: no pin shows", true, bestTile, false);
        SetPresentationMode(ArmyActionType.FoundCity);
        Gate("a mode the advisor does not own keeps a vanilla pin answer on", true, bestTile, true);
        Gate("a mode the advisor does not own keeps a vanilla pin answer off", false, pickTile, false);
        SetPresentationMode(ArmyActionType.CreateCamp);

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
            !Inactive() && AscendingUnique(PickSet.Current.Tiles));
        Gate("sea-flagged territory: pick pins are forced", false, 132, true);
        surface.AllOcean = false;
        surface.DistrictFrame++;
        Refresh(settlement, 134); // restore: land picks on the same tiles
        Check("flag off restores the same picks",
            !Inactive());

        // D. The !Ready half of the Run gate: a not-ready refresh writes nothing in either mode
        // and deactivates.
        surface.ReadyFlag = false;
        Refresh(city, 5003);
        Check("not-ready city refresh: zero writes, pick set inactive", Signature(city) == clean && Inactive());
        var settlementBeforeNotReady = Signature(settlement);
        Refresh(settlement, 131);
        Check("not-ready settlement refresh: zero writes, pick set inactive",
            Signature(settlement) == settlementBeforeNotReady && Inactive());
        surface.ReadyFlag = true;

        // E. Mode switch on the shared evaluation: a city activation must inherit no settlement state.
        // Vanilla Initialize only lowers ValidTileCount, so the grown array and the written tail
        // slots of the settlement session are exactly what the city session then scores: the advisor
        // has to release them. Its own slots (>= baseline) are checked separately from
        // vanilla's ([0, baseline)), which must stay untouched.
        settlement.SettleType = ArmyActionType.FoundCity;
        settlement.ValidTileCount = baseline; // what vanilla's Initialize does on the city activation
        var vanillaSlots = VanillaSlots(settlement, baseline);
        plugin.Writer.NoteActivation(settlement);
        bool tailReleased = true;
        for (int i = baseline + 1; i < settlement.ValidTiles.Length; i++)
            if (settlement.ValidTiles[i].TileIndex != -1) tailReleased = false;
        Refresh(settlement, 131);
        Check("city activation on the shared array releases the settlement tail", tailReleased);
        Check("city activation on the shared array leaves vanilla's slots alone",
            VanillaSlots(settlement, baseline) == vanillaSlots && Inactive());
        Check("a mode switch leaks no suppressed glyph: vanilla's flags survive the settlement session",
            VanillaGlyphs(settlement, baseline) == settlementGlyphs);

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

        // G. The camp pipeline at the takeover (ticket 13). Vanilla's full camp evaluation is
        // skipped, the scan pass publishes its admission set on the baseline the advisor
        // reserves, and the best-of-2 cascade arrays stay retired.
        var scanEval = NewEvaluation(ArmyActionType.Ransack, baseline); // the takeover stamps the mode
        var scanSnapshot = CreateSnapshot();
        bool vanillaRuns = ScanPass(scanSnapshot, scanEval, ArmyActionType.CreateCamp, 131);
        Check("camp takeover skips vanilla's full camp evaluation", !vanillaRuns);
        Check("camp takeover stamps the mode on the evaluation", scanEval.SettleType == ArmyActionType.CreateCamp);
        Check("camp takeover reserves the activation baseline",
            scanEval.ValidTileCount == 0 && scanEval.ValidTiles.Length >= 1 + EvaluationWriter.TailCapacity);
        Check("camp takeover publishes the admission set", !Inactive() && AscendingUnique(PickSet.Current.Tiles));
        Check("camp takeover writes no pick entries", ScanTailHits(scanEval, PickSet.Current.Tiles, 1) == 0);
        Check("camp takeover leaves the best-camp arrays retired",
            AllMinusOne(scanEval.BestCampPlacementTileIndexes) && AllMinusOne(scanEval.BestCampPlacementIndexes));
        Check("no mod code reads the best-camp array: a populated best tile outside the set shows no pin",
            !GateAnswer(bestTile, true));

        canvas.Started = 0;
        canvas.Stopped = 0;
        plugin.Stars.Render(PickSet.Current);
        int trackedStars = canvas.Started;
        int liveStars = trackedStars; // fx the renderer currently tracks, in members of the last set rendered
        Check("the takeover's admission set renders one star per member",
            trackedStars == PickSet.Current.Tiles.Length);

        // The army-level gate: any hit publishes INACTIVE, writes nothing, asks no tile, and
        // displays nothing: stars cleared, no pin, no vanilla surface taking the advice's place.
        surface.ArmyGateFlags = ArmyActionFailureFlags_Part1.ArmyLocked;
        surface.TileGateCalls = 0;
        Refresh(scanEval, 131);
        Check("army-level gate hit publishes INACTIVE", Inactive());
        canvas.Started = 0;
        canvas.Stopped = 0;
        plugin.Stars.Render(PickSet.Current);
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

        // Unmapped and untaken modes: vanilla stays fully in charge, the advisor publishes INACTIVE.
        var foreignEval = NewEvaluation(ArmyActionType.Ransack, baseline);
        var foreignClean = Signature(foreignEval);
        Check("an unmapped mode leaves vanilla fully in charge", ScanPass(scanSnapshot, foreignEval, ArmyActionType.Ransack, 131));
        Check("an unmapped mode publishes INACTIVE", Inactive());
        Check("an unmapped mode writes nothing", Signature(foreignEval) == foreignClean);

        var cityScan = NewEvaluation(ArmyActionType.FoundCity, baseline);
        var cityScanClean = Signature(cityScan);
        Check("city founding is not taken over in this slice", ScanPass(scanSnapshot, cityScan, ArmyActionType.FoundCity, 131));
        Check("city founding publishes INACTIVE at the takeover", Inactive());
        Check("city founding keeps its vanilla evaluation untouched", Signature(cityScan) == cityScanClean);

        // Admission on the refresh: quota 2 with 1 tile at rank 1 and 2 tiles tied at rank 2
        // publishes 3 (SPEC section 2 step 8), every admitted tile gets a star, and pins follow
        // the published membership.
        surface.ScoreByTile[131] = 90;
        surface.ScoreByTile[132] = 50;
        surface.ScoreByTile[133] = 50;
        surface.DistrictFrame++;
        Refresh(scanEval, 131);
        var admitted = PickSet.Current.Tiles;
        Check("quota 2 plus the rank-2 tie set publishes 3 tiles",
            admitted.Length == 3 && admitted[0] == 131 && admitted[1] == 132 && admitted[2] == 133);
        canvas.Started = 0;
        canvas.Stopped = 0;
        plugin.Stars.Render(PickSet.Current);
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
            GateAnswer(admitted[0]) && GateAnswer(admitted[1]) && GateAnswer(admitted[2]));

        // A member three territories deep in the placement neighborhood - the far side of the
        // map from the cursor: its star renders, the gate admits it, and the on-screen culling
        // that keeps its pin off screen is vanilla's, unoverridden.
        Plugin.CfgRangeLevels.Value = 3;
        surface.DistrictFrame++;
        Refresh(scanEval, 101);
        var neighborhood = PickSet.Current.Tiles;
        canvas.Started = 0;
        canvas.Stopped = 0;
        plugin.Stars.Render(PickSet.Current);
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
            !Inactive() && PickSet.Current.Tiles.Length == 3 && PickSet.Current.Contains(131));
        surface.TileGateFlags[131] = ArmyActionFailureFlags_Part1.TileIsOccupied;
        surface.DistrictFrame++;
        Refresh(scanEval, 131);
        Check("a non-cost gate failure drops the tile", !Inactive() && !PickSet.Current.Contains(131));
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
        plugin.Stars.Render(PickSet.Current);
        Check("INACTIVE clears every tracked star", canvas.Stopped == liveStars && canvas.Started == 0);
        Check("zero picks show no pin: neither a former member nor a vanilla best tile",
            !GateAnswer(131, true) && !GateAnswer(5001, true));
        surface.AllWater = false;
        liveStars = 0;

        // The resource-tile exclusion is a rule of the pipeline, not of the gate.
        surface.ResourceTiles.Add(133);
        surface.DistrictFrame++;
        Refresh(scanEval, 131);
        Check("the resource tile is excluded from admission", !Inactive() && !PickSet.Current.Contains(133));
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
        var hoveredSet = PickSet.Current.Tiles;
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
            scanEval.ValidTiles.Length == cursorSlot + 1 + EvaluationWriter.TailCapacity);
        // The gate reads the presentation evaluation of the snapshot - the same instance a
        // refresh writes in the game, so the harness points it at the evaluation under test.
        snapshot.PresentationData.PlacementEvaluation = scanEval;
        canvas.Started = 0;
        canvas.Stopped = 0;
        plugin.Stars.Render(PickSet.Current);
        Check("hovering the unaffordable pick stops no star, and the tile stays a member",
            canvas.Stopped == 0 && ContainsTile(hoveredSet, 131));
        Check("the pick slot entry for the same tile is the affordable pick's shape: shown, scored",
            ShowableIsMembership(scanEval, 0, hoveredSet));
        // The pin gate's hovered rule (SettlementPlacementPinsSubset.cs:29-33) answers from the
        // flags the advisor wrote: money drops the pin on hover, influence-only keeps it, a clean
        // hover keeps it. Membership, stars and glyph flags never move.
        Check("the pin gate's hovered rule reads the advisor-written cursor-slot flags",
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
            PickSet.Current.Tiles.Length == hoveredSet.Length && ShowableIsMembership(scanEval, 0, hoveredSet));
        surface.TileGateFlags.Clear();
        Check("every advisor slot above the live count is zeroed, cursor slot window included",
            ZeroFilledAboveCount(scanEval, scanEval.ValidTileCount,
                cursorSlot + 1 + EvaluationWriter.TailCapacity));
        var keptVanilla = NewEvaluation(ArmyActionType.FoundCity, baseline);
        var keptSignature = Signature(keptVanilla);
        Check("a mode not taken over keeps vanilla's own refresh, its own cursor entry included",
            Refresh(keptVanilla, 5001) && Signature(keptVanilla) == keptSignature && Inactive());

        // A cursor outside the world answers nothing: the cursor slot scrubs and the cursor index
        // points at nothing.
        Refresh(scanEval, 5099);
        Check("a cursor with no territory scrubs the cursor slot and points the index at nothing",
            scanEval.CurrentPositionIndex == -1 && scanEval.ValidTiles[cursorSlot].TileIndex == -1
            && scanEval.SettleFailure == ArmyActionFailureFlags.None);
        Console.WriteLine("[noopc] verdict: " + (s_Failures == 0 ? "GREEN" : "RED (" + s_Failures + " contract violations)"));
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
}
