using System;
using System.Threading;
using Amplitude.Mercury.Interop;

namespace CampPlacementAdvisor
{
    /// <summary>
    /// THE EVALUATION WRITER: puts the computed picks into the game's camp placement
    /// evaluation so the FIMS pins have data, and suppresses the vanilla placement glyphs.
    /// The tail is ONE FIXED window of array indices reserved at activation: slot
    /// TailStart is the cursor slot, vanilla's own append point after a full-eval
    /// Initialize (UpdateCurrentPosition appends the cursor entry exactly at
    /// ValidTileCount = baseline count); slots TailStart+1.. hold the picks, rewritten in
    /// place at INVARIANT indices every refresh. Vanilla entries are never moved, so the
    /// index-bound presentation (pins bind pins to array indices) never rebinds a retained
    /// pick: the cursor tile's element is the only display element that changes between
    /// refreshes inside one pick set. Vanilla's per-cursor-tile appends land above the
    /// tail (append point >= tail end once the tail is live). ValidTileCount is raised to
    /// the tail end only after the entries are written and a memory barrier, so the
    /// presentation thread iterating [0, ValidTileCount) never sees a hole. Simulation
    /// thread only.
    /// </summary>
    internal sealed class EvaluationWriter
    {
        /// <summary>
        /// Headroom of the tail window: pick slots (hovered pair + MaxRangeTerritories
        /// capped) + the cursor slot, rounded up. Bounds the writer's own footprint.
        /// </summary>
        public const int TailCapacity = 128;

        private static readonly Pick[] s_Empty = new Pick[0];

        private int m_TailStart = -1; // cursor slot index (fixed for the session); -1 = no baseline

        /// <summary>
        /// Activation baseline (postfix on CampPlacementEvaluation.Initialize, sim thread):
        /// the vanilla valid positions are final here, so record the baseline count (the
        /// cursor slot index = the tail base) and reserve the whole tail window before the
        /// cursor starts refreshing.
        /// </summary>
        public void NoteActivation(CampPlacementEvaluation evaluation)
        {
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
        /// Rewrite the pick slots in place (ascending unique tiles, simulation thread).
        /// When the cursor tile is one of the picks, also own CurrentPositionIndex: vanilla's
        /// UpdateCurrentPosition found the cursor tile at a slot this Write overwrites, so
        /// repoint it at the pick slot holding the cursor tile (the ghost preview must land
        /// on the cursor tile, not on whatever pick now occupies the old index).
        /// </summary>
        public void Write(CampPlacementEvaluation evaluation, Pick[] picks, int cursorTile)
        {
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

            // Picks in place at invariant slots. The cursor slot (m_TailStart) is never
            // written: it is vanilla's own cursor entry slot after a full-eval Initialize.
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

        /// <summary>Drop the advisor's contribution (advisor inactive / guard path).</summary>
        public void ClearTail(CampPlacementEvaluation evaluation)
        {
            Write(evaluation, s_Empty, -1);
        }
    }
}
