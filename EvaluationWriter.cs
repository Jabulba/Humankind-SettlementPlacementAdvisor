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
    /// Puts picks into the settlement placement evaluation for the FIMS pins: the advisor-written
    /// slots carry the shown set, so a published member is the entry's Showable flag and vanilla's
    /// own entries keep their own glyphs. Settlement placement writes only: FoundCity and
    /// FoundCityWithSettler share this evaluation type, so the mutating entry point guards on the
    /// mode-policy row and a city activation only scrubs a tail left by an earlier settlement
    /// session.
    ///
    /// Each evaluation gets one fixed tail window, reserved at its activation: TailStart is the
    /// cursor slot, and the advisor writes it itself on every refresh of a mode it has taken over,
    /// which is why vanilla's append-one-tile hover rule is dead in that mode. The slots above it
    /// hold the picks, rewritten at invariant indices. Vanilla entries never move, so index-bound
    /// pins never rebind a retained pick. A window is tracked per evaluation instance because the
    /// range overlay's evaluation is a second live instance of the same type (SPEC section 7), and
    /// every slot above the live count is zeroed (the Initialize(-1) shape vanilla skips), the
    /// writer's own duty from the reservation on. ValidTileCount rises only after the entries are
    /// written and a memory barrier passed, so the presentation thread never sees a hole.
    /// Simulation thread.
    /// </summary>
    internal sealed class EvaluationWriter
    {
        /// <summary>Headroom over the cursor slot plus the capped pick count.</summary>
        public const int TailCapacity = 128;

        private static readonly Pick[] s_Empty = new Pick[0];

        /// <summary>
        /// Vanilla's activation call, the tail-reservation point the takeover replaces. Plain
        /// reflection, not Harmony helpers: the harnesses drive this code with no patcher loaded.
        /// </summary>
        private static readonly MethodInfo InitializeMethod = typeof(CampPlacementEvaluation).GetMethod(
            "Initialize", BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(CustomTileIndexArea) }, null);

        /// <summary>The cursor slot index of one evaluation's reserved window.</summary>
        private sealed class TailWindow
        {
            public int TailStart = -1; // fixed for this evaluation's session; -1 = not reserved
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
        /// positions are final, so record the cursor slot and reserve the tail before refreshes
        /// start. SettleType is assigned before Initialize runs, so it is authoritative here. The
        /// reserved window is zero-filled on the way in: the slots above the count the advisor owns
        /// start as the shape vanilla skips.
        /// </summary>
        public void NoteActivation(CampPlacementEvaluation evaluation)
        {
            if (!ModePolicy.IsWired(evaluation.SettleType))
            {
                // Vanilla scores ValidTiles[0..Length), not [0..ValidTileCount), and skips only
                // entries under 25 FIMS, so surviving settlement picks would compete with real city
                // candidates. Scrub the tail an earlier settlement session left on this evaluation.
                ReleaseTail(evaluation);
                return;
            }

            var window = WindowFor(evaluation);
            window.TailStart = evaluation.ValidTileCount;

            int capacity = window.TailStart + 1 + TailCapacity;
            var tiles = evaluation.ValidTiles;
            int grown = tiles == null ? 0 : tiles.Length;
            if (tiles == null || tiles.Length < capacity)
            {
                Array.Resize(ref tiles, capacity);
                evaluation.ValidTiles = tiles;
            }
            var flags = evaluation.ConditionalFlagsTileIndexes;
            if (flags != null && flags.Length < capacity)
            {
                Array.Resize(ref flags, capacity);
                evaluation.ConditionalFlagsTileIndexes = flags;
            }
            ZeroFill(evaluation, Math.Max(window.TailStart, grown), capacity);

            var plugin = Plugin.Instance;
            if (plugin != null)
                plugin.Log("activation baseline count=" + window.TailStart + " capacity=" + evaluation.ValidTiles.Length);
        }

        /// <summary>
        /// The activation baseline in a taken-over mode. Vanilla's full settlement evaluation was
        /// the only Initialize caller in camp mode (SimulationEvaluator.cs:2332); with the
        /// takeover replacing it, the advisor runs Initialize itself over an empty candidate area.
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
        /// Rewrite the tail for one refresh (simulation thread): the cursor slot first, from the
        /// advisor's unmasked gate call - the failure flags and the cost trio land on the
        /// evaluation, where the camp mouse marker, the territory highlight, the camp mouse marker
        /// and vanilla's hovered-pin rule read them, and the entry is the hovered tile's own scored
        /// record with no glyph - then each member's pick slot, written Showable, so the glyph set
        /// is the published membership. Slots this refresh released, and every slot above the count,
        /// are zeroed. The cursor index points at the cursor slot; the no-entry shape points it at
        /// nothing. Non-settlement evaluations return without writing.
        /// </summary>
        public void Write(CampPlacementEvaluation evaluation, Pick[] picks, CursorSlot cursor)
        {
            if (!ModePolicy.IsWired(evaluation.SettleType))
                return;

            var window = WindowFor(evaluation);
            var tiles = evaluation.ValidTiles;
            int count = evaluation.ValidTileCount;

            if (window.TailStart < 0)
                window.TailStart = count; // activation patch never ran: baseline from the current count

            int tailEnd = window.TailStart + 1 + picks.Length; // cursor slot + pick slots
            int needed = Math.Max(count, tailEnd);
            int windowEnd = window.TailStart + 1 + TailCapacity; // the window this writer owns

            int grown = tiles == null ? 0 : tiles.Length;
            int capacity = Math.Max(needed, windowEnd);
            if (tiles == null || tiles.Length < capacity)
            {
                Array.Resize(ref tiles, capacity);
                evaluation.ValidTiles = tiles;
                var grew = Plugin.Instance;
                if (grew != null)
                    grew.Log("tail capacity grown to " + tiles.Length);
            }
            var flags = evaluation.ConditionalFlagsTileIndexes;
            if (flags != null && flags.Length < capacity)
            {
                Array.Resize(ref flags, capacity);
                evaluation.ConditionalFlagsTileIndexes = flags;
            }

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

            // Picks go at invariant slots above the cursor slot. Showable is advisor-written
            // membership: the published set is the only glyph source in the advisor's slots, and
            // vanilla's own entries keep their own glyphs.
            for (int i = 0; i < picks.Length; i++)
            {
                var entry = picks[i].Data;
                entry.Showable = true;
                tiles[window.TailStart + 1 + i] = entry;
                if (flags != null)
                    flags[window.TailStart + 1 + i] = DistrictPlacementConditionFlags.None;
            }

            // Slots the previous refresh owned and this one released, and the slots the array
            // growth just created: zeroed, so a stale pick keeps no glyph, no pin slot and no
            // score, and nothing above the live count is ever read (SPEC section 6 row 3).
            ZeroFill(evaluation, tailEnd, Math.Max(needed, Math.Max(capacity, grown)));

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

        /// <summary>
        /// Release the whole window this advisor reserved on this evaluation, cursor slot included,
        /// and let the next activation re-baseline. Needed on a mode switch because Write keeps its
        /// entries, and vanilla Initialize never zeroes them. Zeroed entries are skipped by vanilla;
        /// no-op when no tail was reserved.
        /// </summary>
        private void ReleaseTail(CampPlacementEvaluation evaluation)
        {
            TailWindow window;
            if (!m_Windows.TryGetValue(evaluation, out window) || window.TailStart < 0)
                return;

            var tiles = evaluation.ValidTiles;
            if (tiles != null)
                ZeroFill(evaluation, window.TailStart, Math.Min(tiles.Length, window.TailStart + 1 + TailCapacity));

            var flags = evaluation.ConditionalFlagsTileIndexes;
            if (flags != null)
            {
                int end = Math.Min(flags.Length, window.TailStart + 1 + TailCapacity);
                for (int i = window.TailStart; i < end; i++)
                    flags[i] = DistrictPlacementConditionFlags.None;
            }

            window.TailStart = -1; // the next settlement activation re-baselines
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
        private void ZeroFill(CampPlacementEvaluation evaluation, int from, int to)
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
