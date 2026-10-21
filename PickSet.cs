using System;

namespace CampPlacementAdvisor
{
    /// <summary>
    /// THE PUBLISHED PICK SET: an immutable snapshot of the tiles the advisor selected for the
    /// current cursor context, carrying its own entering/leaving diff against the previous pick
    /// set. THE ONLY cross-thread channel: built on the simulation thread, read by the star
    /// renderer and the pin gate on the presentation thread. Readers capture PickSet.Current
    /// ONCE per pass and answer every membership question from that capture.
    /// </summary>
    internal sealed class PickSet
    {
        /// <summary>Current published pick set; null = advisor inactive. Volatile: single-word swap.</summary>
        public static volatile PickSet Current;

        public readonly int Version;
        public readonly int[] Tiles;      // ascending, unique; null = inactive
        public readonly int[] Entering;   // tiles in this pick set, absent from the previous one
        public readonly int[] Leaving;    // tiles in the previous pick set, absent from this one

        private PickSet(int version, int[] tiles, int[] entering, int[] leaving)
        {
            Version = version;
            Tiles = tiles;
            Entering = entering;
            Leaving = leaving;
        }

        /// <summary>
        /// Membership in THIS snapshot instance (binary search over Tiles). Callers capture
        /// PickSet.Current once; this never re-reads the volatile field, so a pass cannot tear.
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
        /// Publish a pick set: ascending unique tile indices, or null for inactive (simulation
        /// thread). Content-identical republish is a no-op, so moving the mouse inside a territory
        /// publishes nothing and churns no fx. The entering/leaving diff is computed here, at the
        /// seam, so no consumer recomputes membership.
        /// </summary>
        public static void Publish(int[] tiles)
        {
            var prev = Current;
            if (tiles == null && (prev == null || prev.Tiles == null))
                return; // already inactive: publishing nothing bumps nothing
            if (prev != null && tiles != null && prev.Tiles != null && tiles.Length == prev.Tiles.Length && SameTiles(prev.Tiles, tiles))
                return;

            int version = prev == null ? 1 : prev.Version + 1;
            int[] entering;
            int[] leaving;
            Diff(prev == null ? null : prev.Tiles, tiles, out entering, out leaving);

            Current = new PickSet(version, tiles, entering, leaving);

            var plugin = Plugin.Instance;
            if (plugin != null)
                plugin.Log("publish v=" + version
                    + " picks=" + (tiles == null ? 0 : tiles.Length)
                    + " entering=" + entering.Length
                    + " leaving=" + leaving.Length);
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
}
