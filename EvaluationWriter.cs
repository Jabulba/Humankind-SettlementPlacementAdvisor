using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Amplitude; // FixedPoint (assembly Amplitude.Framework.dll).
using Amplitude.Mercury.Interop;
using Amplitude.Mercury.Data.Simulation; // ArmyActionType.
using Amplitude.Mercury.Simulation; // CustomTileIndexArea

namespace SettlementPlacementAdvisor
{
    /// <summary>
    /// What the advisor read about the hovered tile for one refresh, as the writer writes it into
    /// the cursor slot: the tile, its own scored entry, and the mode gate call's answer with no
    /// mask - the failure flags plus the three instant costs the camp mouse marker shows. The
    /// no-entry shape (Answered false) is the advisor's claim that nothing is known about the
    /// hovered tile: the cursor slot is scrubbed and the cursor index points at nothing.
    /// </summary>
    internal struct CursorSlot
    {
        public bool Answered;
        public int Tile;
        public DistrictPlacementEvaluation.ValidTile Entry;
        public ArmyActionFailureFlags Failure;
        public FixedPoint MoneyCost;
        public FixedPoint InfluenceCost;
        public FixedPoint UnitCost;

        /// <summary>The no-entry shape: scrubbed slot, cleared failure flags and costs.</summary>
        public static readonly CursorSlot Unanswered = new CursorSlot();
    }

    /// <summary>
    /// Puts picks into the placement evaluation the mode session is running, for the FIMS pins: the
    /// advisor-written slots carry the shown set, so a published member is the entry's Showable flag
    /// and vanilla's own entries keep their own glyphs. Writes happen only through the active mode's
    /// advisor pipeline, into that mode's evaluation only (SPEC section 4): the city no-op contract
    /// is retired, so the mutating entry points guard on the mode-policy row - a mode the table has
    /// not wired writes nothing - and an activation releases the tail a previous mode's session left
    /// on the shared evaluation, in every mode.
    ///
    /// Each evaluation gets one tail window, reserved at its activation: TailStart is the
    /// cursor slot, and the advisor writes it itself on every refresh of a mode it has taken over,
    /// which is why vanilla's append-one-tile hover rule is dead in that mode. The slots above it
    /// hold the picks, rewritten at invariant indices. Vanilla entries never move, so index-bound
    /// pins never rebind a retained pick. The window is sized from config at the activation - the
    /// worst-case admission set for the configured hops and quotas - and grows on demand, tracked
    /// per evaluation instance because the range overlay's evaluation is a second live instance of
    /// the same type (SPEC section 7). A release scrubs every slot the region ever covered, and
    /// every slot above the live count is the shape vanilla skips: the writer restores that
    /// invariant incrementally from its previous write, never by sweeping the array. ValidTileCount
    /// rises only after the entries are written and a memory barrier passed, so the presentation
    /// thread never sees a hole. Simulation thread.
    /// </summary>
    internal sealed class EvaluationWriter
    {
        private static readonly Pick[] s_Empty = new Pick[0];

        /// <summary>
        /// Vanilla's activation call, the tail-reservation point the takeover replaces. Plain
        /// reflection, not Harmony helpers: the harnesses drive this code with no patcher loaded.
        /// </summary>
        private static readonly MethodInfo InitializeMethod = typeof(CampPlacementEvaluation).GetMethod(
            "Initialize", BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(CustomTileIndexArea) }, null);

        /// <summary>One evaluation's tail window. TailStart is the cursor slot; ReservedEnd is the
        /// exclusive end of every slot this evaluation has ever reserved (the config-derived reserve,
        /// then any grow-on-demand); WrittenEnd is the exclusive end of the last refresh's slots, the
        /// frontier the next refresh zeroes back from.</summary>
        private sealed class TailWindow
        {
            public int TailStart = -1; // fixed for this evaluation's session; -1 = not reserved
            public int ReservedEnd; // exclusive end of the region ever reserved here
            public int WrittenEnd; // exclusive end of the last refresh's written slots
        }

        // Reference-keyed: each evaluation instance (the cursor snapshot's, the range overlay's)
        // owns its own window. Simulation thread only, like every other writer field.
        private readonly Dictionary<CampPlacementEvaluation, TailWindow> m_Windows =
            new Dictionary<CampPlacementEvaluation, TailWindow>();

        /// <summary>
        /// The cursor slot's invariant index in one evaluation: the base of its reserved window,
        /// fixed for the session. -1 while no tail is reserved.
        /// </summary>
        public int CursorSlotIndex(CampPlacementEvaluation evaluation)
        {
            TailWindow window;
            return m_Windows.TryGetValue(evaluation, out window) ? window.TailStart : -1;
        }

        /// <summary>
        /// Activation baseline (postfix on Initialize, simulation thread): vanilla's valid
        /// positions are final, so record the cursor slot and reserve this session's window before
        /// refreshes start. SettleType is assigned before Initialize runs, so it is authoritative
        /// here. The activation releases the whole region a previous session reserved first, in every
        /// mode: vanilla Initialize only lowers ValidTileCount, so a settlement tail left by a camp
        /// session is exactly what a city session would then score, and a tail is scrubbed, never
        /// merely counted down (SPEC section 6 rows 1 and 4). The reserved window is zero-filled on
        /// the way in: the slots above the count the advisor owns start as the shape vanilla skips.
        /// </summary>
        public void NoteActivation(CampPlacementEvaluation evaluation)
        {
            // Vanilla scores ValidTiles[0..Length), not [0..ValidTileCount), and skips only entries
            // under 25 FIMS, so a surviving tail competes with the mode that follows. Release it
            // before any mode re-baselines, including this writer's own mode.
            ReleaseTail(evaluation);

            if (!ModePolicy.IsWired(evaluation.SettleType))
                return; // no row in the mode-policy table: vanilla's mode, vanilla's evaluation

            var window = Reserve(evaluation, evaluation.ValidTileCount);

            var plugin = Plugin.Instance;
            if (plugin != null)
                plugin.Log("activation baseline count=" + window.TailStart + " reserved=" + window.ReservedEnd);
        }

        /// <summary>
        /// The activation baseline in a taken-over mode. Vanilla's full settlement evaluation was
        /// the only Initialize caller in a wired mode (SimulationEvaluator.cs:2332, the settlement
        /// and city founding branches); with the takeover replacing it, the advisor runs Initialize
        /// itself over an empty candidate area.
        /// The Initialize postfix then reserves the tail from this baseline, and the best-camp
        /// arrays come back all -1, which is how the retired best-of-2 cascade leaves them.
        /// </summary>
        public void ReserveBaseline(CampPlacementEvaluation evaluation)
        {
            if (InitializeMethod == null)
            {
                GameApi.Fail("CampPlacementEvaluation.Initialize(CustomTileIndexArea)");
                return;
            }
            InitializeMethod.Invoke(evaluation, new object[] { null });

            // In the game the Initialize postfix reserves the tail from the new baseline. The
            // harness runs no patcher, so the reservation is taken here as well: it re-reads the
            // same baseline, so doing it twice reserves the same window.
            NoteActivation(evaluation);
        }

        /// <summary>
        /// Rewrite the tail for one refresh of a wired mode (simulation thread): the cursor slot
        /// first, from the advisor's unmasked gate call - the failure flags and the cost trio land
        /// on the evaluation, where the ghost, the territory highlight, the settlement mouse marker
        /// and vanilla's hovered-pin rule read them, and the entry is the hovered tile's own scored
        /// record with no glyph - then each member's pick slot, written Showable, so the glyph set
        /// is the published membership. Slots the previous refresh owned and this one released are
        /// zeroed, and nothing wider: the writer zero-fills incrementally from its last write and
        /// never sweeps the array (SPEC section 6 row 3). The cursor index points at the cursor slot;
        /// the no-entry shape points it at nothing. A mode the mode-policy table has not wired
        /// returns without writing.
        /// </summary>
        public void Write(CampPlacementEvaluation evaluation, Pick[] picks, CursorSlot cursor)
        {
            if (!ModePolicy.IsWired(evaluation.SettleType))
                return; // no row in the mode-policy table: the mode's evaluation stays vanilla's

            var window = WindowFor(evaluation);
            if (window.TailStart < 0)
                Reserve(evaluation, evaluation.ValidTileCount); // activation patch never ran: baseline from the current count

            int count = evaluation.ValidTileCount;
            int tailEnd = window.TailStart + 1 + picks.Length; // cursor slot + pick slots
            int needed = Math.Max(count, tailEnd);
            if (needed > window.ReservedEnd)
                Grow(window, evaluation, needed); // grow-on-demand past the derived reserve

            var tiles = evaluation.ValidTiles;
            var flags = evaluation.ConditionalFlagsTileIndexes;

            // The cursor slot: the advisor's own TailStart entry, and the single surviving path to
            // the ghost, the territory highlight, the camp mouse marker and the hovered-pin rule in
            // a mode the advisor has taken over.
            var slot = cursor.Answered ? cursor.Entry : Scrubbed();
            slot.Showable = false; // no glyph on the cursor slot: the pick slots own the shown set
            tiles[window.TailStart] = slot;
            if (flags != null)
                flags[window.TailStart] = DistrictPlacementConditionFlags.None;
            evaluation.SettleFailure = cursor.Failure;
            evaluation.InstantMoneyCost = cursor.MoneyCost;
            evaluation.InstantInfluenceCost = cursor.InfluenceCost;
            evaluation.InstantUnitCost = cursor.UnitCost;

            // Picks go at invariant slots above the cursor slot: vanilla binds pins to array indices
            // (PlacementPin.cs:125-128), so a pick that stays in the set stays in its slot and every
            // retained pin stays bound. Showable is advisor-written membership: the published set is
            // the only glyph source in the advisor's slots, and vanilla's own entries keep their own
            // glyphs.
            for (int i = 0; i < picks.Length; i++)
            {
                var entry = picks[i].Data;
                entry.Showable = true;
                tiles[window.TailStart + 1 + i] = entry;
                if (flags != null)
                    flags[window.TailStart + 1 + i] = DistrictPlacementConditionFlags.None;
            }

            // Zero the slots the previous refresh owned and this one released: a stale pick keeps no
            // glyph, no pin slot and no score. Slots the window reserves and never writes came off
            // the reservation already scrubbed, and slots outside the window are vanilla's append
            // space: no refresh sweeps the array length (SPEC section 6 row 3).
            ZeroFill(evaluation, tailEnd, window.WrittenEnd);
            window.WrittenEnd = tailEnd;

            Thread.MemoryBarrier(); // release: tail entries are visible before the count rises

            evaluation.CurrentPositionIndex = cursor.Answered ? window.TailStart : -1;
            evaluation.ValidTileCount = needed;
        }

        /// <summary>
        /// Drop the advisor's contribution (inactive or failure path): the cursor slot scrubs to
        /// the no-entry shape, the pick slots scrub with it, and the cursor index points at
        /// nothing. INACTIVE displays nothing, so no cursor entry stays live.
        /// </summary>
        public void ClearTail(CampPlacementEvaluation evaluation)
        {
            Write(evaluation, s_Empty, CursorSlot.Unanswered);
        }

        /// <summary>Take this evaluation's tail window at `baseline`: the cursor slot plus the
        /// config-derived reserve above it (SPEC section 6 row 1), zero-filled on the way in. The
        /// reserved region only ever grows on an evaluation, so a release covers every slot the
        /// writer took.</summary>
        private TailWindow Reserve(CampPlacementEvaluation evaluation, int baseline)
        {
            var window = WindowFor(evaluation);
            window.TailStart = baseline;
            window.WrittenEnd = baseline + 1; // only the cursor slot has been written
            var regionEnd = baseline + 1 + TailCapacity();
            if (regionEnd > window.ReservedEnd)
                window.ReservedEnd = regionEnd;
            EnsureCapacity(evaluation, window.ReservedEnd);
            ZeroFill(evaluation, baseline, window.ReservedEnd);
            return window;
        }

        /// <summary>The tail reserve for the config this activation reads: the worst-case
        /// admission-set size for the configured hops and quotas, on the same knobs the scan's
        /// CursorContext reads. A reserve, never a cap on admission.</summary>
        private static int TailCapacity()
        {
            return AdvisorConfig.TailCapacityFor(
                Plugin.CfgTopHovered.Value, Plugin.CfgTopNeighbour.Value, Plugin.CfgRangeLevels.Value);
        }

        /// <summary>Grow-on-demand: an admission set past the derived reserve extends the region
        /// this evaluation has ever reserved to cover it, and the release scrubs the grown
        /// region.</summary>
        private void Grow(TailWindow window, CampPlacementEvaluation evaluation, int regionEnd)
        {
            window.ReservedEnd = regionEnd;
            EnsureCapacity(evaluation, regionEnd);
            var plugin = Plugin.Instance;
            if (plugin != null)
                plugin.Log("tail region grown to " + regionEnd);
        }

        /// <summary>The array side of reserving and growing: resize both arrays to the region end,
        /// and give the fresh slots the Initialize(-1) shape - Array.Resize hands back default
        /// entries, and a default entry is tile 0, not a scrubbed slot.</summary>
        private static void EnsureCapacity(CampPlacementEvaluation evaluation, int regionEnd)
        {
            var tiles = evaluation.ValidTiles;
            int grown = tiles == null ? 0 : tiles.Length;
            if (tiles == null || tiles.Length < regionEnd)
            {
                Array.Resize(ref tiles, regionEnd);
                evaluation.ValidTiles = tiles;
            }
            var flags = evaluation.ConditionalFlagsTileIndexes;
            if (flags != null && flags.Length < regionEnd)
            {
                Array.Resize(ref flags, regionEnd);
                evaluation.ConditionalFlagsTileIndexes = flags;
            }
            if (grown < regionEnd)
                ZeroFill(evaluation, grown, regionEnd);
        }

        /// <summary>
        /// Release every slot this advisor ever reserved on this evaluation - cursor slot, derived
        /// reserve and grow-on-demand included - and let the next activation re-baseline. Every
        /// activation calls it, in every mode, because Write keeps its entries and vanilla
        /// Initialize never zeroes them: a settlement tail left by a camp session is scrubbed on the
        /// city activation, the city tail on the settler one, and no settlement entry survives a
        /// switch as a phantom recommendation in any mode. Zeroed entries are skipped by vanilla;
        /// no-op when no tail was reserved (SPEC section 6 row 4).
        /// </summary>
        private void ReleaseTail(CampPlacementEvaluation evaluation)
        {
            TailWindow window;
            if (!m_Windows.TryGetValue(evaluation, out window) || window.TailStart < 0)
                return;

            ZeroFill(evaluation, window.TailStart, window.ReservedEnd); // the full region, not a constant window

            window.TailStart = -1; // the next activation re-baselines
            window.ReservedEnd = 0;
            window.WrittenEnd = 0;
        }

        private TailWindow WindowFor(CampPlacementEvaluation evaluation)
        {
            TailWindow window;
            if (!m_Windows.TryGetValue(evaluation, out window))
            {
                window = new TailWindow();
                m_Windows[evaluation] = window;
            }
            return window;
        }

        /// <summary>The scrubbed entry shape vanilla skips: no tile, no FIMS, no glyph.</summary>
        private static DistrictPlacementEvaluation.ValidTile Scrubbed()
        {
            var cleared = default(DistrictPlacementEvaluation.ValidTile);
            cleared.Initialize(-1);
            return cleared;
        }

        /// <summary>Zero the advisor-owned slots in [from, to): the writer's zero-fill duty above
        /// the live count (SPEC section 6 row 3).</summary>
        private static void ZeroFill(CampPlacementEvaluation evaluation, int from, int to)
        {
            var tiles = evaluation.ValidTiles;
            if (tiles != null)
            {
                int end = Math.Min(tiles.Length, to);
                for (int i = Math.Max(from, 0); i < end; i++)
                    tiles[i] = Scrubbed();
            }

            var flags = evaluation.ConditionalFlagsTileIndexes;
            if (flags != null)
            {
                int end = Math.Min(flags.Length, to);
                for (int i = Math.Max(from, 0); i < end; i++)
                    flags[i] = DistrictPlacementConditionFlags.None;
            }
        }
    }
}
