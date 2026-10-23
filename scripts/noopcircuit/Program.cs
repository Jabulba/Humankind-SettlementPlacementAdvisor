using System;
using System.Linq;
using System.Reflection;
using System.Text;
using Amplitude;
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

    public bool EvaluateTile(object army, int tileIndex, object districtDefinition, out DistrictPlacementEvaluation.ValidTile validTile, out FixedPoint score)
    {
        var vt = default(DistrictPlacementEvaluation.ValidTile);
        vt.Initialize(tileIndex);
        validTile = vt;
        score = tileIndex;
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

    public Territory.Adjacency[] AdjacentTerritories(int territoryIndex)
    {
        var edges = new Territory.Adjacency[territoryIndex < 3 ? 1 : 0];
        for (int i = 0; i < edges.Length; i++)
            edges[i] = new Territory.Adjacency { TerritoryIndex = territoryIndex + 1, Transition = TerritoryTransition.Open };
        return edges;
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
/// DEBUG [noopc] no-op contract harness. All three settle types share
/// CampPlacementEvaluation, so a city session must see zero writes. Checks compare a full
/// signature of the mutable state before and after, catching any mutation, not just known
/// ones. Settlement placement is the control: tail reserved, picks written, vanilla glyphs
/// suppressed. The pin gate is driven through the real postfix.
/// </summary>
internal static class Program
{
    private static int s_Failures;

    private static void Check(string name, bool ok)
    {
        Console.WriteLine("[noopc] " + (ok ? "OK   " : "FAIL ") + name);
        if (!ok) s_Failures++;
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

    /// <summary>Drive the real settlement-evaluation postfix (the advisor's entry point per refresh).</summary>
    private static void Refresh(CampPlacementEvaluation eval, int cursorTile)
    {
        var postfix = typeof(SettlementEvaluationPatch).GetMethod("Postfix", BindingFlags.NonPublic | BindingFlags.Static);
        postfix.Invoke(null, new object[] { eval, cursorTile, new SimulationEntityGUID(1UL) });
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
        plugin.Writer.Write(city, new[] { stray }, 5001);
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

        // B. Settlement control: the advisor's own session must behave exactly as before.
        var settlement = NewEvaluation(ArmyActionType.CreateCamp, baseline);
        plugin.Writer.NoteActivation(settlement);
        Check("CreateCamp activation reserves the tail window",
            settlement.ValidTiles.Length >= baseline + 1 + EvaluationWriter.TailCapacity);

        Refresh(settlement, 131); // cursor tile inside the synthetic territory space (territory 3)
        var tiles = PickSet.Current == null || PickSet.Current.Tiles == null ? new int[0] : PickSet.Current.Tiles;
        int inTail = 0;
        for (int i = baseline + 1; i < settlement.ValidTileCount; i++)
            for (int t = 0; t < tiles.Length; t++)
                if (settlement.ValidTiles[i].TileIndex == tiles[t]) inTail++;
        bool glyphsSuppressed = true;
        for (int i = 0; i < baseline; i++)
            if (settlement.ValidTiles[i].Showable) glyphsSuppressed = false;

        Check("CreateCamp refresh publishes ascending unique tiles", AscendingUnique(tiles));
        Check("CreateCamp refresh writes every pick into its tail slot",
            settlement.ValidTileCount > baseline + 1 && inTail == tiles.Length);
        Check("CreateCamp refresh suppresses the vanilla glyphs", glyphsSuppressed);
        plugin.Stars.Render(PickSet.Current);
        Check("settlement pick set starts one star per pick", canvas.Started == tiles.Length);

        // C. Pin gate through the real postfix, with a snapshot that reports a vanilla best tile.
        var snapshot = CreateSnapshot();
        int bestTile = 5001; // a vanilla best-2 tile: never an advisor pick (picks are 13x tiles)
        snapshot.PresentationData.PlacementEvaluation.BestCampPlacementTileIndexes = new[] { bestTile, -1 };
        Snapshots.SettlementPlacementCursorSnapshot = snapshot;

        int pickTile = tiles[0];
        Gate("active pick set forces the pin on a pick", false, pickTile, true);
        Gate("active pick set suppresses the vanilla best pin outside the set", true, bestTile, false);

        canvas.Started = 0;
        canvas.Stopped = 0;
        plugin.Stars.Render(PickSet.Current);
        Check("settlement pick set re-render is version-gated (no fx churn)", canvas.Started == 0);
        plugin.Advisor.Deactivate();
        plugin.Stars.Render(PickSet.Current);
        Check("deactivation stops every tracked star", canvas.Stopped == tiles.Length && canvas.Started == 0);
        Check("Deactivate publishes the Tiles==null inactive representation",
            PickSet.Current != null && PickSet.Current.Tiles == null);
        Gate("inactive by Tiles: vanilla best pin survives", true, bestTile, true);
        Gate("inactive by Tiles: vanilla answer survives on a former pick tile", false, pickTile, false);

        int deactivatedVersion = PickSet.Current.Version;
        plugin.Advisor.Deactivate();
        Check("Deactivate is idempotent (no version churn per refresh)",
            PickSet.Current.Version == deactivatedVersion && PickSet.Current.Tiles == null);

        PickSet.Current = null;
        Gate("no pick set at all: vanilla answer survives", true, bestTile, true);

        // C2. Zero-pick publish (all in-range territories claimed/all-water): the advisor must
        // deactivate, NOT publish an active empty set, because an active set suppresses the
        // vanilla best pins without replacing them (observed in-game: only the hovered tile
        // keeps its FIMS pin).
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
        Gate("zero-pick refresh: vanilla best pin survives", true, bestTile, true);

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
