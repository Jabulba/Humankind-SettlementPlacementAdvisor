using System;
using System.Reflection;
using System.Threading;
using Amplitude.Mercury.Interop;
using Amplitude.Mercury.Data.Simulation; // ArmyActionType.
using Amplitude.Mercury.Simulation; // CustomTileIndexArea

namespace SettlementPlacementAdvisor
{
    /// <summary>
    /// Puts picks into the settlement placement evaluation for the FIMS pins: the advisor-written
    /// slots carry the shown set, so a published member is the entry's Showable flag and vanilla's
    /// own entries keep their own glyphs. Settlement placement writes only: FoundCity and
    /// FoundCityWithSettler share this evaluation type, so the mutating entry point guards on
    /// SettleType and a city activation only scrubs a tail left by an earlier settlement session.
    ///
    /// The tail is one fixed window reserved at activation. TailStart is the cursor slot,
    /// vanilla's append point after a full-eval Initialize; slots above it hold the picks,
    /// rewritten at invariant indices. Vanilla entries never move, so index-bound pins never
    /// rebind a retained pick. ValidTileCount rises only after the entries are written and a
    /// memory barrier passed, so the presentation thread never sees a hole. Simulation thread.
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

        private int m_TailStart = -1; // cursor slot index (fixed for the session); -1 = no baseline

        /// <summary>
        /// Activation baseline (postfix on Initialize, simulation thread): vanilla's valid
        /// positions are final, so record the cursor slot and reserve the tail before refreshes
        /// start. SettleType is assigned before Initialize runs, so it is authoritative here.
        /// </summary>
        public void NoteActivation(CampPlacementEvaluation evaluation)
        {
            if (evaluation.SettleType != ArmyActionType.CreateCamp)
            {
                // Vanilla scores ValidTiles[0..Length), not [0..ValidTileCount), and skips only
                // entries under 25 FIMS, so surviving settlement picks would compete with real city
                // candidates. Scrub the tail an earlier settlement session left on this evaluation.
                ReleaseTail(evaluation);
                return;
            }

            m_TailStart = evaluation.ValidTileCount;

            int capacity = m_TailStart + 1 + TailCapacity;
            var tiles = evaluation.ValidTiles;
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

            var plugin = Plugin.Instance;
            if (plugin != null)
                plugin.Log("activation baseline count=" + m_TailStart + " capacity=" + evaluation.ValidTiles.Length);
        }

        /// <summary>
        /// The activation baseline in a taken-over mode. Vanilla's full settlement evaluation was
        /// the only Initialize caller in camp mode (SimulationEvaluator.cs:2332); with the
        /// takeover replacing it, the advisor runs Initialize itself over an empty candidate area.
        /// The Initialize postfix then reserves the tail from this baseline, vanilla's hover append
        /// finds a real array to append its cursor entry into (CampPlacementEvaluation.cs:75-81
        /// dereferences ValidTiles.Length), and the best-camp arrays come back all -1, which is
        /// how the retired best-of-2 cascade leaves them.
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
        /// Rewrite the pick slots in place (ascending unique tiles, simulation thread): each
        /// member's entry is written Showable, so the glyph set is the published membership, and
        /// the slots this refresh released are zero-filled. If the cursor tile is a pick, repoint
        /// CurrentPositionIndex at that pick's slot so the ghost stays on the cursor tile.
        /// Non-settlement evaluations return without writing.
        /// </summary>
        public void Write(CampPlacementEvaluation evaluation, Pick[] picks, int cursorTile)
        {
            if (evaluation.SettleType != ArmyActionType.CreateCamp)
                return;

            var tiles = evaluation.ValidTiles;
            int count = evaluation.ValidTileCount;

            if (m_TailStart < 0)
                m_TailStart = count; // activation patch never ran: baseline from the current count

            int tailEnd = m_TailStart + 1 + picks.Length; // cursor slot + pick slots
            int needed = Math.Max(count, tailEnd);

            if (tiles == null || tiles.Length < needed)
            {
                Array.Resize(ref tiles, needed + TailCapacity);
                evaluation.ValidTiles = tiles;
                var grown = Plugin.Instance;
                if (grown != null)
                    grown.Log("tail capacity grown to " + tiles.Length);
            }
            var flags = evaluation.ConditionalFlagsTileIndexes;
            if (flags != null && flags.Length < needed)
            {
                Array.Resize(ref flags, needed + TailCapacity);
                evaluation.ConditionalFlagsTileIndexes = flags;
            }

            // Picks go at invariant slots; TailStart itself is vanilla's cursor entry slot.
            // Showable is advisor-written membership: the published set is the only glyph source
            // in the advisor's slots, and vanilla's own entries keep their own glyphs.
            for (int i = 0; i < picks.Length; i++)
            {
                var entry = picks[i].Data;
                entry.Showable = true;
                tiles[m_TailStart + 1 + i] = entry;
                if (flags != null)
                    flags[m_TailStart + 1 + i] = DistrictPlacementConditionFlags.None;
            }

            // Slots the previous refresh owned and this one released: zeroed, so no stale pick
            // keeps a glyph, a pin slot, or a score inside the live count.
            for (int i = tailEnd; i < needed; i++)
            {
                var released = default(DistrictPlacementEvaluation.ValidTile);
                released.Initialize(-1);
                tiles[i] = released;
                if (flags != null)
                    flags[i] = DistrictPlacementConditionFlags.None;
            }

            Thread.MemoryBarrier(); // release: tail entries are visible before the count rises

            if (cursorTile >= 0)
            {
                for (int i = m_TailStart + 1; i < tailEnd; i++)
                {
                    if (tiles[i].TileIndex == cursorTile)
                    {
                        evaluation.CurrentPositionIndex = i; // pick slot: the cursor's own entry
                        break;
                    }
                }
            }

            evaluation.ValidTileCount = needed;
        }

        /// <summary>
        /// Drop the advisor's contribution (inactive or failure path). Count can never shrink
        /// below what a previous Write raised (vanilla's cursor entry may sit above the tail),
        /// so the pick slots are also scrubbed: stale pick tiles must not sit inside the live
        /// ValidTileCount window (a duplicate of vanilla's best tile would render two pins).
        /// </summary>
        public void ClearTail(CampPlacementEvaluation evaluation)
        {
            Write(evaluation, s_Empty, -1);
            ReleaseTail(evaluation);
        }

        /// <summary>
        /// Scrub only the slots this advisor reserved. Needed on a mode switch because Write
        /// lowers the count but keeps its entries, and vanilla Initialize never zeroes them.
        /// Zeroed entries are skipped by vanilla; no-op when no tail was reserved.
        /// </summary>
        private void ReleaseTail(CampPlacementEvaluation evaluation)
        {
            if (m_TailStart < 0)
                return;

            var tiles = evaluation.ValidTiles;
            if (tiles != null)
            {
                int end = Math.Min(tiles.Length, m_TailStart + 1 + TailCapacity);
                for (int i = m_TailStart + 1; i < end; i++)
                {
                    var cleared = default(DistrictPlacementEvaluation.ValidTile);
                    cleared.Initialize(-1); // Showable=false, zero FIMS: vanilla skips this slot
                    tiles[i] = cleared;
                }
            }

            var flags = evaluation.ConditionalFlagsTileIndexes;
            if (flags != null)
            {
                int end = Math.Min(flags.Length, m_TailStart + 1 + TailCapacity);
                for (int i = m_TailStart + 1; i < end; i++)
                    flags[i] = DistrictPlacementConditionFlags.None;
            }

            m_TailStart = -1; // the next settlement activation re-baselines
        }
    }
}
