using System;
using System.Reflection;
using System.Text;
using Amplitude;
using Amplitude.Mercury.Data.Simulation; // ArmyActionType
using Amplitude.Mercury.Interop;
using Amplitude.Mercury.Simulation;
using SettlementPlacementAdvisor;

/// <summary>
/// Synthetic surface: four territories of three camp-site tiles, scored by tile index, so a
/// territory's best tiles are its last two. No game or sandbox involved.
/// </summary>
internal sealed class GraphSurface : GameSurface
{
    public bool ReadyFlag = true;

    public bool Ready { get { return ReadyFlag; } }
    public object TryGetArmy(SimulationEntityGUID guid) { return "army"; }
    public int GetArmyEmpireIndex(object army) { return 0; }
    public bool IsCampableTile(int tileIndex) { return true; }
    public bool IsTileExplored(int tileIndex, int empireIndex) { return true; }

    public bool EvaluateTile(object army, int tileIndex, out DistrictPlacementEvaluation.ValidTile validTile, out FixedPoint score)
    {
        var vt = default(DistrictPlacementEvaluation.ValidTile);
        vt.Initialize(tileIndex);
        validTile = vt;
        score = tileIndex;
        return true;
    }

    public bool GetCurrentFrames(out int districtFrame, out int constructionFrame, out int visibilityFrame)
    {
        districtFrame = 1; constructionFrame = 1; visibilityFrame = 1;
        return true;
    }

    public int TerritoryOfTile(int tileIndex)
    {
        int t = (tileIndex - 100) / 10;
        return (t < 0 || t > 3) ? -1 : t;
    }

    public bool GetTerritoryFacts(int territoryIndex, out bool isOcean, out bool isClaimed, out int[] tiles)
    {
        isOcean = false; isClaimed = false;
        tiles = new int[] { 100 + territoryIndex * 10 + 1, 100 + territoryIndex * 10 + 2, 100 + territoryIndex * 10 + 3 };
        return true;
    }

    public int[] LandAdjacentTerritories(int territoryIndex)
    {
        return territoryIndex < 3 ? new int[] { territoryIndex + 1 } : new int[0];
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
/// ones. Camp placement is the control: tail reserved, picks written, vanilla glyphs
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

    /// <summary>Drive the real camp-evaluation postfix (the advisor's entry point per refresh).</summary>
    private static void Refresh(CampPlacementEvaluation eval, int cursorTile)
    {
        var postfix = typeof(CampEvaluationPatch).GetMethod("Postfix", BindingFlags.NonPublic | BindingFlags.Static);
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

        // B. Camp control: the advisor's own session must behave exactly as before.
        var camp = NewEvaluation(ArmyActionType.CreateCamp, baseline);
        plugin.Writer.NoteActivation(camp);
        Check("CreateCamp activation reserves the tail window",
            camp.ValidTiles.Length >= baseline + 1 + EvaluationWriter.TailCapacity);

        Refresh(camp, 131); // cursor tile inside the synthetic territory space (territory 3)
        var tiles = PickSet.Current.Tiles;
        int inTail = 0;
        for (int i = baseline + 1; i < camp.ValidTileCount; i++)
            for (int t = 0; t < tiles.Length; t++)
                if (camp.ValidTiles[i].TileIndex == tiles[t]) inTail++;
        bool glyphsSuppressed = true;
        for (int i = 0; i < baseline; i++)
            if (camp.ValidTiles[i].Showable) glyphsSuppressed = false;

        Check("CreateCamp refresh publishes ascending unique tiles", AscendingUnique(tiles));
        Check("CreateCamp refresh writes every pick into its tail slot",
            camp.ValidTileCount > baseline + 1 && inTail == tiles.Length);
        Check("CreateCamp refresh suppresses the vanilla glyphs", glyphsSuppressed);
        plugin.Stars.Render(PickSet.Current);
        Check("camp pick set starts one star per pick", canvas.Started == tiles.Length);

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
        Check("camp pick set re-render is version-gated (no fx churn)", canvas.Started == 0);
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

        // D. The !Ready half of the Run gate: a not-ready refresh writes nothing in either mode
        // and deactivates.
        surface.ReadyFlag = false;
        Refresh(city, 5003);
        Check("not-ready city refresh: zero writes, pick set inactive", Signature(city) == clean && Inactive());
        var campBeforeNotReady = Signature(camp);
        Refresh(camp, 131);
        Check("not-ready camp refresh: zero writes, pick set inactive",
            Signature(camp) == campBeforeNotReady && Inactive());
        surface.ReadyFlag = true;

        // E. Mode switch on the shared evaluation: a city activation must inherit no camp state.
        // Vanilla Initialize only lowers ValidTileCount, so the grown array and the written tail
        // slots of the camp session are exactly what the city session then scores: the advisor
        // has to release them. Its own slots (>= baseline) are checked separately from
        // vanilla's ([0, baseline)), which must stay untouched.
        camp.SettleType = ArmyActionType.FoundCity;
        camp.ValidTileCount = baseline; // what vanilla's Initialize does on the city activation
        var vanillaSlots = VanillaSlots(camp, baseline);
        plugin.Writer.NoteActivation(camp);
        bool tailReleased = true;
        for (int i = baseline + 1; i < camp.ValidTiles.Length; i++)
            if (camp.ValidTiles[i].TileIndex != -1) tailReleased = false;
        Refresh(camp, 131);
        Check("city activation on the shared array releases the camp tail", tailReleased);
        Check("city activation on the shared array leaves vanilla's slots alone",
            VanillaSlots(camp, baseline) == vanillaSlots && Inactive());

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
