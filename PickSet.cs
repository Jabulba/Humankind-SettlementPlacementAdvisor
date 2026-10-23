using System;
using System.Threading;
using Amplitude.Mercury.Data.Simulation; // ArmyActionType
using Amplitude.Mercury.Interop; // ArmyActionFailureFlags (the cursor-slot answer)

namespace SettlementPlacementAdvisor
{
    /// <summary>
    /// The immutable pick content of one publish: version, tiles (ascending, unique), and the
    /// entering/leaving diff against the content it replaces. Built on the simulation thread and
    /// read on the presentation thread through the captured presentation view, never mutated after
    /// construction. It gains no fields (SPEC section 5 row 2): the mode and cursor-slot facts the
    /// presentation side reads travel on the view, not here.
    /// </summary>
    internal sealed class PickSet
    {
        public readonly int Version;
        public readonly int[] Tiles;      // ascending, unique; null = inactive
        public readonly int[] Entering;   // tiles in this pick set, absent from the previous one
        public readonly int[] Leaving;    // tiles in the previous pick set, absent from this one

        // Monotonic publish version: every new content claims the next number, so the two
        // publishers (the simulation-thread publish and the mode-exit swap) never share one.
        private static int s_Version;

        private PickSet(int version, int[] tiles, int[] entering, int[] leaving)
        {
            Version = version;
            Tiles = tiles;
            Entering = entering;
            Leaving = leaving;
        }

        /// <summary>
        /// Membership in this content (binary search). Answered on the captured instance, never on a
        /// live channel, so a pass cannot tear mid-way.
        /// </summary>
        public bool Contains(int tileIndex)
        {
            var tiles = Tiles;
            if (tiles == null)
                return false;
            int lo = 0;
            int hi = tiles.Length - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int value = tiles[mid];
                if (value == tileIndex)
                    return true;
                if (value < tileIndex)
                    lo = mid + 1;
                else
                    hi = mid - 1;
            }
            return false;
        }

        /// <summary>
        /// The pick content for one publish of ascending unique tiles, or null for inactive, diffed
        /// against the content it replaces. A content-identical build returns `previous` itself, so
        /// the view's identity check sees it and burns no version: a mouse move inside a territory
        /// churns no fx. The diff is computed here so no consumer recomputes it.
        /// </summary>
        public static PickSet Build(int[] tiles, PickSet previous)
        {
            if (tiles == null && (previous == null || previous.Tiles == null))
                return previous; // already inactive: publishing nothing builds nothing
            if (previous != null && tiles != null && previous.Tiles != null
                && tiles.Length == previous.Tiles.Length && SameTiles(previous.Tiles, tiles))
                return previous;

            int[] entering;
            int[] leaving;
            Diff(previous == null ? null : previous.Tiles, tiles, out entering, out leaving);
            return new PickSet(Interlocked.Increment(ref s_Version), tiles, entering, leaving);
        }

        // Both inputs ascending unique (null = empty side); outputs ascending.
        private static void Diff(int[] previous, int[] next, out int[] entering, out int[] leaving)
        {
            if (previous == null)
            {
                entering = next == null ? new int[0] : (int[]) next.Clone();
                leaving = new int[0];
                return;
            }
            if (next == null)
            {
                entering = new int[0];
                leaving = (int[]) previous.Clone();
                return;
            }

            var enteringBuffer = new int[next.Length];
            var leavingBuffer = new int[previous.Length];
            int i = 0;
            int j = 0;
            int e = 0;
            int l = 0;
            while (i < next.Length && j < previous.Length)
            {
                int a = next[i];
                int b = previous[j];
                if (a == b)
                {
                    i++;
                    j++;
                }
                else if (a < b)
                {
                    enteringBuffer[e++] = a;
                    i++;
                }
                else
                {
                    leavingBuffer[l++] = b;
                    j++;
                }
            }
            while (i < next.Length)
                enteringBuffer[e++] = next[i++];
            while (j < previous.Length)
                leavingBuffer[l++] = previous[j++];

            entering = e == next.Length ? enteringBuffer : Trim(enteringBuffer, e);
            leaving = l == previous.Length ? leavingBuffer : Trim(leavingBuffer, l);
        }

        private static int[] Trim(int[] source, int length)
        {
            var result = new int[length];
            Array.Copy(source, result, length);
            return result;
        }

        private static bool SameTiles(int[] a, int[] b)
        {
            if (a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i])
                    return false;
            return true;
        }
    }

    /// <summary>
    /// The advisor's single cross-thread channel: one immutable, volatile, version-gated snapshot of
    /// everything the presentation thread is allowed to read - the published pick content, the
    /// placement mode it was published for, and the cursor slot's gate answer (vanilla's hovered rule
    /// input). The simulation thread publishes it as one volatile reference swap; the pin gate, the
    /// star renderer and the mode-exit path capture it once per pass and answer every question from
    /// the capture, so no presentation reader touches the live evaluation's mutable fields.
    ///
    /// Publishing is version-gated on the whole presentation content: identical content swaps
    /// nothing, so a mouse move inside a territory churns no fx and an idempotent deactivation burns
    /// no version. Mode exit is the one non-simulation publisher: it swaps the same channel to the
    /// inactive content, which is a single volatile write, so it needs no lock.
    /// </summary>
    internal sealed class PresentationView
    {
        /// <summary>The published view. Boot state: INACTIVE under an unwired mode, so the pin gate
        /// and the renderer stay vanilla's until the advisor publishes for a wired mode. Volatile:
        /// readers capture one reference, so a publish never tears a pass mid-read.</summary>
        public static volatile PresentationView Current = Boot;

        private static PresentationView Boot
        {
            get { return new PresentationView(0, null, ArmyActionType.AttackArmy, ArmyActionFailureFlags.None); }
        }

        public readonly int Version;    // the pick-content version: the star-reconcile gate
        public readonly PickSet Picks;  // immutable content; null, or Tiles null, = advisor inactive
        public readonly ArmyActionType Mode;
        public readonly ArmyActionFailureFlags SettleFailure; // the cursor slot's unmasked gate answer

        private PresentationView(int version, PickSet picks, ArmyActionType mode, ArmyActionFailureFlags settleFailure)
        {
            Version = version;
            Picks = picks;
            Mode = mode;
            SettleFailure = settleFailure;
        }

        /// <summary>The advisor owns this view: it published an active pick set.</summary>
        public bool IsActive
        {
            get { return Picks != null && Picks.Tiles != null; }
        }

        /// <summary>Membership in the captured pick content.</summary>
        public bool Contains(int tileIndex)
        {
            var picks = Picks;
            return picks != null && picks.Contains(tileIndex);
        }

        /// <summary>
        /// Publish ascending unique tiles (null for inactive) with the mode they were published for
        /// and the cursor slot's gate answer: one atomic swap of the single channel. Identical
        /// presentation content publishes nothing.
        /// </summary>
        public static void Publish(int[] tiles, ArmyActionType mode, ArmyActionFailureFlags settleFailure)
        {
            var current = Current;
            var picks = PickSet.Build(tiles, current == null ? null : current.Picks);
            var version = picks == null ? 0 : picks.Version;
            if (current != null && current.Picks == picks && current.Version == version
                && current.Mode == mode && current.SettleFailure == settleFailure)
                return; // nothing a reader sees has changed: no swap, no version burned

            Current = new PresentationView(version, picks, mode, settleFailure);

            var plugin = Plugin.Instance;
            if (plugin != null)
                plugin.Log("publish v=" + version
                    + " picks=" + (tiles == null ? 0 : tiles.Length)
                    + " entering=" + (picks == null ? 0 : picks.Entering.Length)
                    + " leaving=" + (picks == null ? 0 : picks.Leaving.Length));
        }

        /// <summary>
        /// The advisor's deactivation: publish the inactive content for `mode` with no cursor-slot
        /// failure. Displays nothing in a mode the table wires; a mode it does not wire keeps
        /// vanilla's own surface.
        /// </summary>
        public static void PublishInactive(ArmyActionType mode)
        {
            Publish(null, mode, ArmyActionFailureFlags.None);
        }
    }
}
