using System;
using System.Threading;
using Amplitude.Mercury.Interop;
using Amplitude.Mercury.Data.Simulation; // ArmyActionType.

namespace SettlementPlacementAdvisor
{
    /// <summary>
    /// THE EVALUATION WRITER: puts the computed picks into the game's camp placement
    /// evaluation so the FIMS pins have data, and suppresses the vanilla placement glyphs.
    /// CAMP PLACEMENT WRITES ONLY: the mutating entry point guards on SettleType, so the
    /// FoundCity and FoundCityWithSettler sessions that share this evaluation type are never
    /// written, resized, or re-styled. Their activation gets one cleanup in return: a tail
    /// left by an earlier camp session is scrubbed, because vanilla scores
    /// ValidTiles[0..Length), not [0..ValidTileCount).
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
        /// the vanilla valid positions are final here, so record the baseline count (the cursor
        /// slot index = the tail base) and reserve the whole tail window before the cursor
        /// starts refreshing. Camp activations only: SettleType is assigned before Initialize
        /// runs (SettlementPlacementCursorSnapshot.EvaluatePlacement), so it is authoritative
        /// here, and a city placement activation is ignored entirely.
        /// </summary>
        public void NoteActivation(CampPlacementEvaluation evaluation)
        {
            if (evaluation.SettleType != ArmyActionType.CreateCamp)
            {
                // FoundCity / FoundCityWithSettler: no resize, no tail, no log. Release a tail an
                // earlier camp session left on this same evaluation: vanilla scores
                // ValidTiles[0..Length), not [0..ValidTileCount), and skips only entries under 25
                // FIMS (CampPlacementEvaluation.MinimalFimsForCampPlacement), so surviving camp
                // picks would compete with real city candidates for the best-placement slots.
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
        /// Rewrite the pick slots in place (ascending unique tiles, simulation thread).
        /// When the cursor tile is one of the picks, also own CurrentPositionIndex: vanilla's
        /// UpdateCurrentPosition found the cursor tile at a slot this Write overwrites, so
        /// repoint it at the pick slot holding the cursor tile (the ghost preview must land
        /// on the cursor tile, not on whatever pick now occupies the old index). Camp placement
        /// only: the gate at the top of the method makes a non-camp evaluation a clean return.
        /// </summary>
        public void Write(CampPlacementEvaluation evaluation, Pick[] picks, int cursorTile)
        {
            if (evaluation.SettleType != ArmyActionType.CreateCamp)
                return; // the writer's only mutating entry point: the no-op is self-enforcing

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

        /// <summary>
        /// Scrub the slots this advisor reserved (never vanilla's), for a release that has to
        /// survive a mode switch. Write() lowers the count but keeps its slot entries, and
        /// vanilla's Initialize only ever lowers ValidTileCount, so a later non-camp session on
        /// the same evaluation would still see them: its best-placement loop runs over
        /// ValidTiles.Length and skips only entries under 25 FIMS. Zeroed entries are skipped;
        /// no-op when no tail was reserved.
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
