using System;
using System.Threading;
using Amplitude.Mercury.Interop;
using Amplitude.Mercury.Data.Simulation; // ArmyActionType.

namespace SettlementPlacementAdvisor
{
    /// <summary>
    /// Puts picks into the camp placement evaluation for the FIMS pins and suppresses the
    /// vanilla glyphs. Camp placement writes only: FoundCity and FoundCityWithSettler share
    /// this evaluation type, so the mutating entry point guards on SettleType and a city
    /// activation only scrubs a tail left by an earlier camp session.
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
                // entries under 25 FIMS, so surviving camp picks would compete with real city
                // candidates. Scrub the tail an earlier camp session left on this evaluation.
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
        /// Rewrite the pick slots in place (ascending unique tiles, simulation thread). If the
        /// cursor tile is a pick, repoint CurrentPositionIndex at that pick's slot so the ghost
        /// stays on the cursor tile. Non-camp evaluations return without writing.
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

            // Suppress vanilla glyphs over every live entry (vanilla space + old tail slots).
            for (int i = 0; i < count; i++)
            {
                var vanilla = tiles[i];
                if (vanilla.Showable)
                {
                    vanilla.Showable = false;
                    tiles[i] = vanilla;
                }
            }

            // Picks go at invariant slots; TailStart itself is vanilla's cursor entry slot.
            for (int i = 0; i < picks.Length; i++)
            {
                var entry = picks[i].Data;
                entry.Showable = false; // advisor stars, not vanilla glyphs
                tiles[m_TailStart + 1 + i] = entry;
                if (flags != null)
                    flags[m_TailStart + 1 + i] = DistrictPlacementConditionFlags.None;
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

        /// <summary>Drop the advisor's contribution (inactive or failure path).</summary>
        public void ClearTail(CampPlacementEvaluation evaluation)
        {
            Write(evaluation, s_Empty, -1);
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

            m_TailStart = -1; // the next camp activation re-baselines
        }
    }
}
